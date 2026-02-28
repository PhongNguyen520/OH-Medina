namespace OH_Medina.Models;

/// <summary>
/// Checkpoint state stored in Apify Key-Value Store (key "STATE").
/// Used to resume scraping from the last processed date.
/// </summary>
public class StateModel
{
    /// <summary>
    /// Last processed recording date in yyyy-MM-dd format (e.g. "2026-02-26").
    /// When resuming, the scraper uses the day after this as StartDate.
    /// </summary>
    public string LastProcessedDate { get; set; } = "";
}
