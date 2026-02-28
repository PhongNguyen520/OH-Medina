using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Playwright;
using OH_Medina.Models;

namespace OH_Medina.Services;

/// <summary>
/// Playwright-based scraper for Medina County (Ohio). Exports pipe-delimited CSV to Apify Key-Value Store.
/// </summary>
public class OhMedinaScraperService
{
    const string StartUrl = "https://recordersearch.co.medina.oh.us/OHMedina/AvaWeb/#/search";

    const string SelectorStartDate = "input[formcontrolname=\"StartDate\"]";
    const string SelectorEndDate = "input[formcontrolname=\"EndDate\"]";
    // Two Search buttons exist (top + bottom); target the top one to avoid strict mode violation.
    const string SelectorSearchButton = "#topFormButtons button[form=\"searchForm\"].yellow";

    IPlaywright? _playwright;
    IBrowser? _browser;
    IBrowserContext? _context;
    IPage? _page;

    /// <summary>
    /// Main entry: runs the full scrape workflow. State/checkpoint resume from LastProcessedDate.
    /// </summary>
    public async Task RunAsync(InputConfig input)
    {
        input ??= new InputConfig();

        await ApifyHelper.SetStatusMessageAsync("Starting OH-Medina scraper...");

        // Apify State/Checkpoint — resume from last processed date if present
        try
        {
            var state = await ApifyHelper.GetValueAsync<StateModel>("STATE");
            if (state != null && !string.IsNullOrWhiteSpace(state.LastProcessedDate))
            {
                if (DateTime.TryParse(state.LastProcessedDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastDate))
                {
                    var resumeStart = lastDate.AddDays(1).ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
                    input.StartDate = resumeStart;
                    await ApifyHelper.SetStatusMessageAsync($"Resuming from checkpoint: StartDate set to {input.StartDate}...");
                    Console.WriteLine($"[OH_Medina] Resuming from checkpoint: StartDate set to {resumeStart} (last processed: {state.LastProcessedDate})");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OH_Medina] State load failed (continuing with full range): {ex.Message}");
        }

        try
        {
            await InitBrowserAsync();

            _page = await _context!.NewPageAsync();
            _page.SetDefaultTimeout(30_000);

            await _page.GotoAsync(StartUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            await ApifyHelper.SetStatusMessageAsync($"Searching dates: {input.StartDate} to {input.EndDate}...");
            await SearchByDateAsync(input.StartDate, input.EndDate);

            var records = await ExtractDataAsync();

            var dateKey = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await ExportToCsvAndUploadAsync(records, dateKey);
            await ApifyHelper.SetStatusMessageAsync("Success: All records exported to CSV and Dataset.", isTerminal: true);
        }
        catch (Exception ex)
        {
            await ApifyHelper.SetStatusMessageAsync($"Fatal Error: {ex.Message}", isTerminal: true);
            throw;
        }
    }

    /// <summary>Fills the date range and submits the search form. Waits for results to start loading.</summary>
    async Task SearchByDateAsync(string startDate, string endDate)
    {
        if (_page == null) return;

        var startInput = _page.Locator(SelectorStartDate);
        await startInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        await startInput.FillAsync(startDate);
        await _page.Locator(SelectorEndDate).FillAsync(endDate);

        await _page.Locator(SelectorSearchButton).ClickAsync();

        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(3000);
    }

    /// <summary>Extract data from the current results page: expand each row, map to OhMedinaRecord, then collapse.</summary>
    async Task<List<OhMedinaRecord>> ExtractDataAsync()
    {
        var records = new List<OhMedinaRecord>();
        if (_page == null) return records;

        var rows = _page.Locator(".resultRow");
        var count = await rows.CountAsync();

        if (count == 0)
        {
            await ApifyHelper.SetStatusMessageAsync("Finished: No records found.", isTerminal: true);
            return records;
        }

        await ApifyHelper.SetStatusMessageAsync($"Found {count} records. Preparing to extract...");

        for (var i = 0; i < count; i++)
        {
            await ApifyHelper.SetStatusMessageAsync($"Processing record {i + 1} of {count}...");

            await WaitForLoadingBackdropHiddenAsync(_page);

            var row = rows.Nth(i);
            var summary = row.Locator(".resultRowSummary");

            await DomClickAsync(summary);
            var detail = row.Locator(".resultRowDetail");
            await detail.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

            var record = await ExtractRecordFromRowAsync(row);

            var rowDetailContainer = row.Locator(".resultRowDetailContainer");
            var imageIcon = rowDetailContainer.Locator("i.fa-file-alt").First;
            if (await imageIcon.CountAsync() > 0 && await imageIcon.IsVisibleAsync())
                record.PdfUrl = await TryDownloadPdfAsync(_page!, record.DocumentNo);

            records.Add(record);
            await ApifyHelper.PushSingleDataAsync(record);

            // After returning from image view, Angular may re-render; reacquire locators and collapse via DOM click.
            row = _page.Locator(".resultRow").Nth(i);
            summary = row.Locator(".resultRowSummary");
            detail = row.Locator(".resultRowDetail");

            await DomClickAsync(summary);
            try
            {
                await detail.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
            }
            catch
            {
                // Some rows keep detail in DOM; best-effort collapse is enough.
            }
        }

        return records;
    }

    async Task<OhMedinaRecord> ExtractRecordFromRowAsync(ILocator row)
    {
        var panel = row.Locator(".resultRowDetail");
        var record = new OhMedinaRecord();

        var docNoBtn = panel.Locator(".resultRowDetailContainer div button").First;
        if (await docNoBtn.CountAsync() > 0)
            record.DocumentNo = (await docNoBtn.TextContentAsync())?.Trim() ?? "";

        var primaryLabels = panel.Locator("label.resultDetailPrimaryContent");
        var primaryCount = await primaryLabels.CountAsync();
        if (primaryCount >= 1)
            record.RecordedDate = (await primaryLabels.Nth(0).TextContentAsync())?.Trim() ?? "";
        if (primaryCount >= 2)
            record.DocumentType = (await primaryLabels.Nth(1).TextContentAsync())?.Trim() ?? "";

        record.Consideration = await GetLabelValueAfterAsync(panel, "Additional", "Consideration:");
        record.Notes = await GetLabelValueAfterAsync(panel, "Additional", "Notes:");

        record.Party1 = await ExtractListValuesAsync(panel, "Parties", "Party 1:");
        record.Party2 = await ExtractListValuesAsync(panel, "Parties", "Party 2:");
        record.Legals = await ExtractListValuesAsync(panel, "Legals", null);
        record.AssociatedDocuments = await ExtractListValuesAsync(panel, "Additional", "Associated Documents:");

        return record;
    }

    /// <summary>Finds the section by header name, then the label containing labelText, and returns the next element's text.</summary>
    static async Task<string> GetLabelValueAfterAsync(ILocator panel, string sectionName, string labelText)
    {
        var section = panel.Locator("div.avaSection").Filter(new LocatorFilterOptions { HasText = sectionName }).First;
        if (await section.CountAsync() == 0) return "";
        var result = await section.EvaluateAsync<string>(@" (section, key) => {
            const labels = Array.from(section.querySelectorAll('label'));
            for (let i = 0; i < labels.length; i++) {
                if ((labels[i].textContent || '').trim().startsWith(key))
                    return (labels[i].nextElementSibling?.textContent || '').trim();
            }
            return '';
        }", labelText);
        return result ?? "";
    }

    /// <summary>Collects text from resultDetailSubContent in a section, optionally from a block starting with subSectionName.</summary>
    static async Task<List<string>> ExtractListValuesAsync(ILocator panel, string sectionName, string? subSectionName)
    {
        var section = panel.Locator("div.avaSection").Filter(new LocatorFilterOptions { HasText = sectionName }).First;
        if (await section.CountAsync() == 0) return new List<string>();

        if (string.IsNullOrEmpty(subSectionName))
        {
            var all = await section.Locator(".resultDetailSubContent").AllTextContentsAsync();
            return all.Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).ToList();
        }

        var list = await section.EvaluateAsync<string[]>(@" (section, subName) => {
            const all = Array.from(section.querySelectorAll('.resultDetailSubSection, .resultDetailSubContent'));
            const startIdx = all.findIndex(el => el.classList.contains('resultDetailSubSection') && (el.textContent?.trim() || '').includes(subName));
            if (startIdx < 0) return [];
            const result = [];
            for (let i = startIdx + 1; i < all.length; i++) {
                if (all[i].classList.contains('resultDetailSubSection')) break;
                result.push((all[i].textContent || '').trim());
            }
            return result.filter(Boolean);
        }", subSectionName);
        return list?.ToList() ?? new List<string>();
    }

    /// <summary>
    /// Downloads the PDF by extracting the Blob data.
    /// Uses advanced JS injection to completely suppress the native print dialog from blocking the execution loop.
    /// </summary>
    async Task<string> TryDownloadPdfAsync(IPage page, string documentNumber)
    {
        var docId = documentNumber;
        var safeFileName = SanitizeFileName(docId) + ".pdf";

        var localDir = Path.Combine("Output", "PDFs");
        Directory.CreateDirectory(localDir);
        var fullPath = Path.Combine(localDir, safeFileName);
        var pdfUrlToReturn = "";

        try
        {
            // Scope the click to the current expanded row (matching this document number),
            // otherwise the first icon on the page may belong to a different record.
            var docButton = page
                .Locator(".resultRowDetailContainer button")
                .GetByText(docId, new LocatorGetByTextOptions { Exact = true })
                .First;
            await docButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

            var rowDetailContainer = docButton.Locator("xpath=ancestor::div[contains(@class,'resultRowDetailContainer')]").First;
            var imageIcon = rowDetailContainer.Locator("i.fa-file-alt").First;
            if (!await imageIcon.IsVisibleAsync())
                return "";

            await DomClickAsync(imageIcon);

            await page.Locator("canvas#imageCanvas").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
            await Task.Delay(1000);

            // --- ADVANCED PRINT SUPPRESSION ---
            // Intercept iframe creation and immediately destroy its ability to call print()
            await page.EvaluateAsync(@"() => {
                window.print = function() { console.log('Main window print blocked'); };

                const originalAppendChild = Node.prototype.appendChild;
                Node.prototype.appendChild = function(node) {
                    if (node && node.tagName && node.tagName.toLowerCase() === 'iframe') {
                        node.addEventListener('load', function() {
                            try {
                                if (this.contentWindow) {
                                    this.contentWindow.print = function() { console.log('Iframe print blocked'); };
                                }
                            } catch (e) {}
                        });
                    }
                    return originalAppendChild.call(this, node);
                };
            }");

            await DomClickAsync(page.Locator("button[title=\"Print\"]").First);

            var dialog = page.Locator("mat-dialog-container").First;
            await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

            var entireDocSpan = dialog.Locator("span").GetByText("Entire Document", new LocatorGetByTextOptions { Exact = true }).First;
            var radioInput = entireDocSpan.Locator("xpath=preceding-sibling::input[@type='radio']").First;
            await radioInput.CheckAsync(new LocatorCheckOptions { Force = true });

            Console.WriteLine($"[OH_Medina] Requesting Blob PDF for {docId}...");

            // We use EvaluateAsync to click the button so Playwright doesn't get stuck waiting
            // if a native dialog somehow blocks the execution thread.
            await page.EvaluateAsync(@"() => {
                const buttons = Array.from(document.querySelectorAll('mat-dialog-container button'));
                const okButton = buttons.find(b => (b.textContent || '').trim() === 'OK');
                if (okButton) okButton.click();
            }");

            // Failsafe: Try to dismiss the print dialog if it appears (works in some UI contexts)
            await Task.Delay(1000);
            await page.Keyboard.PressAsync("Escape");

            var printIframe = page.Locator("iframe#printJS");
            await printIframe.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 60_000 });

            var blobUrl = await printIframe.GetAttributeAsync("src") ?? "";
            if (string.IsNullOrEmpty(blobUrl) || !blobUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Could not find the blob URL in the printJS iframe.");

            var base64String = await page.EvaluateAsync<string>(@"async (url) => {
                const response = await fetch(url);
                const blob = await response.blob();
                return new Promise((resolve, reject) => {
                    const reader = new FileReader();
                    reader.onloadend = () => resolve(reader.result);
                    reader.onerror = reject;
                    reader.readAsDataURL(blob);
                });
            }", blobUrl);

            var commaIndex = base64String.IndexOf(",", StringComparison.Ordinal);
            var base64Data = commaIndex >= 0 ? base64String.Substring(commaIndex + 1) : base64String;
            var pdfBytes = Convert.FromBase64String(base64Data);
            await File.WriteAllBytesAsync(fullPath, pdfBytes);

            Console.WriteLine($"[OH_Medina] Successfully extracted original PDF from Blob: {fullPath}");

            // Cleanup: remove iframe so next record doesn't accidentally reuse it.
            await page.EvaluateAsync(@"() => { document.getElementById('printJS')?.remove(); }");

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APIFY_IS_AT_HOME")))
            {
                await ApifyHelper.SaveKeyValueRecordAsync(safeFileName, pdfBytes, "application/pdf");
                var storeId = Environment.GetEnvironmentVariable("APIFY_DEFAULT_KEY_VALUE_STORE_ID");
                pdfUrlToReturn = string.IsNullOrEmpty(storeId)
                    ? ""
                    : $"https://api.apify.com/v2/key-value-stores/{storeId}/records/{Uri.EscapeDataString(safeFileName)}?disableRedirect=true";
            }
            else
            {
                pdfUrlToReturn = fullPath;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OH_Medina] PDF extraction failed for {docId}: {ex.Message}");
        }
        finally
        {
            try
            {
                // Force Escape again just in case a dialog is lingering
                await page.Keyboard.PressAsync("Escape");

                var backBtn = page.Locator("div.backButton button").First;
                if (await backBtn.IsVisibleAsync())
                {
                    await DomClickAsync(backBtn);
                    await page.Locator("div.searchResults").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
                    await WaitForLoadingBackdropHiddenAsync(page);
                }
            }
            catch { /* ignore on way back */ }
        }

        return pdfUrlToReturn;
    }

    static async Task WaitForLoadingBackdropHiddenAsync(IPage page, int timeoutMs = 15_000)
    {
        var backdrop = page.Locator("#loadingBackDrop");
        if (await backdrop.CountAsync() == 0) return;
        await backdrop.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = timeoutMs });
    }

    static async Task DomClickAsync(ILocator locator)
    {
        await locator.ScrollIntoViewIfNeededAsync();
        try
        {
            // Prefer normal click first (keeps event semantics).
            await locator.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
        }
        catch
        {
            // Fallback: bypass pointer interception by clicking via DOM.
            await locator.EvaluateAsync("el => el.click()");
        }
    }

    static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (Array.IndexOf(invalid, c) >= 0) sb.Append('_');
            else sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Export records to pipe-delimited CSV and upload to Apify Key-Value Store. To be implemented.</summary>
    /// <summary>
    /// Exports the list of records to a pipe-delimited CSV.
    /// If running on Apify, uploads the CSV to the Key-Value Store.
    /// </summary>
    async Task ExportToCsvAndUploadAsync(List<OhMedinaRecord> records, string dateKey)
    {
        if (records == null || records.Count == 0)
        {
            Console.WriteLine("[OH_Medina] No records to export.");
            return;
        }

        var fileName = $"OH-Medina_{dateKey}.csv";
        var localDir = Path.Combine("Output", "CSVs");
        Directory.CreateDirectory(localDir);
        var fullPath = Path.Combine(localDir, fileName);

        try
        {
            Console.WriteLine($"[OH_Medina] Exporting {records.Count} records to CSV...");

            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                Delimiter = "|",
                HasHeaderRecord = true
            };

            using (var writer = new StreamWriter(fullPath))
            using (var csv = new CsvWriter(writer, config))
            {
                csv.WriteRecords(records);
            }

            Console.WriteLine($"[OH_Medina] CSV saved locally at: {fullPath}");

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APIFY_IS_AT_HOME")))
            {
                Console.WriteLine("[OH_Medina] Uploading CSV to Apify Key-Value Store...");
                var csvBytes = await File.ReadAllBytesAsync(fullPath);
                var safeKey = fileName.Replace("/", "-");
                await ApifyHelper.SaveKeyValueRecordAsync(safeKey, csvBytes, "text/csv");
                Console.WriteLine("[OH_Medina] CSV uploaded successfully.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OH_Medina] Error exporting CSV: {ex.Message}");
        }
    }

    async Task InitBrowserAsync()
    {
        _playwright = await Playwright.CreateAsync();
        var isApify = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APIFY_CONTAINER_PORT"));
        var browserArgs = new[]
        {
            "--no-default-browser-check",
            "--disable-dev-shm-usage",
            "--disable-gpu",
            "--no-sandbox",
            "--disable-software-rasterizer",
            "--disable-extensions",
            "--disable-background-networking",
            "--disable-default-apps",
            "--disable-sync",
            "--disable-translate",
            "--mute-audio",
            "--no-first-run",
            "--disable-renderer-backgrounding"
        };
        try
        {
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = "chrome",
                Headless = isApify,
                Timeout = 60_000,
                Args = browserArgs
            });
        }
        catch
        {
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = isApify,
                Timeout = 60_000,
                Args = browserArgs
            });
        }
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });
    }

    /// <summary>
    /// Stops browser and disposes Playwright resources.
    /// </summary>
    public async Task StopAsync()
    {
        if (_page != null)
        {
            await _page.CloseAsync();
            _page = null;
        }
        if (_context != null)
        {
            await _context.CloseAsync();
            _context = null;
        }
        if (_browser != null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }
        _playwright?.Dispose();
        _playwright = null;
        await Task.CompletedTask;
    }
}
