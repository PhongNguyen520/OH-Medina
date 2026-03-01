using System.Globalization;
using Microsoft.Playwright;
using OH_Medina.Models;
using OH_Medina.Utils;

namespace OH_Medina.Services;

/// <summary>Playwright-based scraper for Medina County (Ohio). Exports pipe-delimited CSV to Apify Key-Value Store.</summary>
public class OhMedinaScraperService
{
    const string StartUrl = "https://recordersearch.co.medina.oh.us/OHMedina/AvaWeb/#/search";

    const string SelectorStartDate = "input[formcontrolname=\"StartDate\"]";
    const string SelectorEndDate = "input[formcontrolname=\"EndDate\"]";
    const string SelectorSearchButton = "#topFormButtons button[form=\"searchForm\"].yellow";

    IPlaywright? _playwright;
    IBrowser? _browser;
    IBrowserContext? _context;
    IPage? _page;

    /// <summary>Main entry: runs full scrape workflow using input.StartDate and input.EndDate.</summary>
    public async Task RunAsync(InputConfig input)
    {
        input ??= new InputConfig();

        await ApifyHelper.SetStatusMessageAsync("Starting OH-Medina scraper...");

        try
        {
            await InitBrowserAsync();

            _page = await _context!.NewPageAsync();
            _page.SetDefaultTimeout(30_000);

            int searchRetries = 3;
            bool searchSuccess = false;

            for (int attempt = 1; attempt <= searchRetries; attempt++)
            {
                try
                {
                    if (attempt > 1)
                        await ApifyHelper.SetStatusMessageAsync($"Search attempt {attempt} of {searchRetries}...");

                    await _page.GotoAsync(StartUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                    await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

                    await ApifyHelper.SetStatusMessageAsync($"Searching dates: {input.StartDate} to {input.EndDate}...");
                    await SearchByDateAsync(input.StartDate, input.EndDate);

                    searchSuccess = true;
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Attempt {attempt}] Search failed: {ex.Message}");
                    if (attempt == searchRetries)
                    {
                        await ApifyHelper.SetStatusMessageAsync($"Fatal Error during search after {searchRetries} attempts: {ex.Message}", isTerminal: true);
                        throw;
                    }
                    await Task.Delay(5000);
                }
            }

            if (!searchSuccess) return;

            var (records, total, succeeded, failed) = await ExtractDataAsync();

            var dateKey = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await CsvExportHelper.ExportToCsvAndUploadAsync(records, dateKey);
            await ApifyHelper.SetStatusMessageAsync($"Finished! Total {total} requests: {succeeded} succeeded, {failed} failed.", isTerminal: true);
        }
        catch (Exception ex)
        {
            await ApifyHelper.SetStatusMessageAsync($"Fatal Error: {ex.Message}", isTerminal: true);
            throw;
        }
    }

    /// <summary>Fill date range and submit search form.</summary>
    async Task SearchByDateAsync(string startDate, string endDate)
    {
        if (_page == null) return;

        var startInput = _page.Locator(SelectorStartDate);
        await startInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        await startInput.FillAsync(startDate);
        await _page.Locator(SelectorEndDate).FillAsync(endDate);

        await _page.Locator(SelectorSearchButton).First.ClickAsync();

        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(3000);
    }

    /// <summary>Extract data from results page: expand each row, map to OhMedinaRecord, collapse.</summary>
    async Task<(List<OhMedinaRecord> records, int total, int succeeded, int failed)> ExtractDataAsync()
    {
        var records = new List<OhMedinaRecord>();
        if (_page == null) return (records, 0, 0, 0);

        var rows = _page.Locator(".resultRow");
        var count = await rows.CountAsync();

        if (count == 0)
        {
            await ApifyHelper.SetStatusMessageAsync("Finished: No records found.", isTerminal: true);
            return (records, 0, 0, 0);
        }

        const int TestLimit = 5;
        var toProcess = Math.Min(count, TestLimit);
        if (count > TestLimit)
            Console.WriteLine($"[OH_Medina] Limiting to {TestLimit} records for test (total {count} skipped).");

        await ApifyHelper.SetStatusMessageAsync($"Found {toProcess} records. Preparing to extract...");

        var succeeded = 0;
        var failed = 0;

        for (var i = 0; i < toProcess; i++)
        {
            await ApifyHelper.SetStatusMessageAsync($"Processing record {i + 1} of {toProcess}...");

            try
            {
                await DomHelper.WaitForLoadingBackdropHiddenAsync(_page);

                var row = rows.Nth(i);
                var summary = row.Locator(".resultRowSummary");

                await DomHelper.DomClickAsync(summary);
                var detail = row.Locator(".resultRowDetail");
                await detail.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

                var record = await ExtractRecordFromRowAsync(row);

                var rowDetailContainer = row.Locator(".resultRowDetailContainer");
                var imageIcon = rowDetailContainer.Locator("i.fa-file-alt").First;
                if (await imageIcon.CountAsync() > 0 && await imageIcon.IsVisibleAsync())
                    record.PdfUrl = await PdfDownloader.TryDownloadPdfAsync(_page, record.DocumentNo);

                records.Add(record);
                await ApifyHelper.PushSingleDataAsync(record);
                succeeded++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OH_Medina] Error processing record {i + 1}: {ex.Message}");
                failed++;
            }

            var row2 = _page.Locator(".resultRow").Nth(i);
            var summary2 = row2.Locator(".resultRowSummary");
            var detail2 = row2.Locator(".resultRowDetail");
            await DomHelper.DomClickAsync(summary2);
            try
            {
                await detail2.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
            }
            catch { }
        }

        return (records, toProcess, succeeded, failed);
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

        record.Consideration = await DomHelper.GetLabelValueAfterAsync(panel, "Additional", "Consideration:");
        record.Notes = await DomHelper.GetLabelValueAfterAsync(panel, "Additional", "Notes:");

        record.Party1 = await DomHelper.ExtractListValuesAsync(panel, "Parties", "Party 1:");
        record.Party2 = await DomHelper.ExtractListValuesAsync(panel, "Parties", "Party 2:");
        record.Legals = await DomHelper.ExtractListValuesAsync(panel, "Legals", null);
        record.AssociatedDocuments = await DomHelper.ExtractListValuesAsync(panel, "Additional", "Associated Documents:");

        return record;
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

    /// <summary>Stops browser and disposes Playwright resources.</summary>
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
    }
}
