using System.IO;
using Microsoft.Playwright;
using OH_Medina.Utils;

namespace OH_Medina.Services;

/// <summary>PDF download via Blob extraction for Medina County records.</summary>
public static class PdfDownloader
{
    /// <summary>Sanitize string for use as file name (replace invalid chars with underscore).</summary>
    public static string SanitizeFileName(string name)
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

    /// <summary>Downloads PDF by extracting Blob data. Suppresses native print dialog via JS injection.</summary>
    public static async Task<string> TryDownloadPdfAsync(IPage page, string documentNumber)
    {
        var docId = documentNumber;
        var safeFileName = SanitizeFileName(docId) + ".pdf";

        var localDir = Path.Combine("Output", "PDFs");
        Directory.CreateDirectory(localDir);
        var fullPath = Path.Combine(localDir, safeFileName);
        var pdfUrlToReturn = "";

        try
        {
            var docButton = page
                .Locator(".resultRowDetailContainer button")
                .GetByText(docId, new LocatorGetByTextOptions { Exact = true })
                .First;
            await docButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

            var rowDetailContainer = docButton.Locator("xpath=ancestor::div[contains(@class,'resultRowDetailContainer')]").First;
            var imageIcon = rowDetailContainer.Locator("i.fa-file-alt").First;
            if (!await imageIcon.IsVisibleAsync())
                return "";

            await DomHelper.DomClickAsync(imageIcon);

            await page.Locator("canvas#imageCanvas").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
            await Task.Delay(1000);

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

            await DomHelper.DomClickAsync(page.Locator("button[title=\"Print\"]").First);

            var dialog = page.Locator("mat-dialog-container").First;
            await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

            var entireDocSpan = dialog.Locator("span").GetByText("Entire Document", new LocatorGetByTextOptions { Exact = true }).First;
            var radioInput = entireDocSpan.Locator("xpath=preceding-sibling::input[@type='radio']").First;
            await radioInput.CheckAsync(new LocatorCheckOptions { Force = true });

            Console.WriteLine($"[OH_Medina] Requesting Blob PDF for {docId}...");

            await page.EvaluateAsync(@"() => {
                const buttons = Array.from(document.querySelectorAll('mat-dialog-container button'));
                const okButton = buttons.find(b => (b.textContent || '').trim() === 'OK');
                if (okButton) okButton.click();
            }");

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
                await page.Keyboard.PressAsync("Escape");

                var backBtn = page.Locator("div.backButton button").First;
                if (await backBtn.IsVisibleAsync())
                {
                    await DomHelper.DomClickAsync(backBtn);
                    await page.Locator("div.searchResults").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
                    await DomHelper.WaitForLoadingBackdropHiddenAsync(page);
                }
            }
            catch { }
        }

        return pdfUrlToReturn;
    }
}
