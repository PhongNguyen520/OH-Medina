using System.Collections.Generic;
using System.Linq;
using Microsoft.Playwright;

namespace OH_Medina.Utils;

/// <summary>DOM manipulation and extraction utilities for Medina County scraper.</summary>
public static class DomHelper
{
    /// <summary>Wait for loading backdrop to become hidden.</summary>
    public static async Task WaitForLoadingBackdropHiddenAsync(IPage page, int timeoutMs = 15_000)
    {
        var backdrop = page.Locator("#loadingBackDrop");
        if (await backdrop.CountAsync() == 0) return;
        await backdrop.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = timeoutMs });
    }

    /// <summary>Click via Playwright or fallback to JS evaluate. Handles overlays/scroll.</summary>
    public static async Task DomClickAsync(ILocator locator)
    {
        await locator.ScrollIntoViewIfNeededAsync();
        try
        {
            await locator.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
        }
        catch
        {
            await locator.EvaluateAsync("el => el.click()");
        }
    }

    /// <summary>Find section by header, then label containing labelText, return next element text.</summary>
    public static async Task<string> GetLabelValueAfterAsync(ILocator panel, string sectionName, string labelText)
    {
        var section = panel.Locator("div.avaSection").Filter(new LocatorFilterOptions { HasText = sectionName }).First;
        if (await section.CountAsync() == 0) return "";
        var result = await section.EvaluateAsync<string>(@"(section, key) => {
            const labels = Array.from(section.querySelectorAll('label'));
            for (let i = 0; i < labels.length; i++) {
                if ((labels[i].textContent || '').trim().startsWith(key))
                    return (labels[i].nextElementSibling?.textContent || '').trim();
            }
            return '';
        }", labelText);
        return result ?? "";
    }

    /// <summary>Collect text from resultDetailSubContent in section, optionally from block starting with subSectionName.</summary>
    public static async Task<List<string>> ExtractListValuesAsync(ILocator panel, string sectionName, string? subSectionName)
    {
        var section = panel.Locator("div.avaSection").Filter(new LocatorFilterOptions { HasText = sectionName }).First;
        if (await section.CountAsync() == 0) return new List<string>();

        if (string.IsNullOrEmpty(subSectionName))
        {
            var all = await section.Locator(".resultDetailSubContent").AllTextContentsAsync();
            return all.Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).ToList();
        }

        var list = await section.EvaluateAsync<string[]>(@"(section, subName) => {
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
}
