namespace OH_Medina.Models;

/// <summary>
/// Input configuration loaded from JSON (local input.json or Apify input).
/// Controls the Medina County search: StartDate, EndDate in MM/DD/YYYY format.
/// </summary>
public class InputConfig
{
    /// <summary>
    /// Start date for the search range. Format: MM/DD/YYYY (e.g. 01/01/2024).
    /// </summary>
    public string StartDate { get; set; } = "";

    /// <summary>
    /// End date for the search range. Format: MM/DD/YYYY (e.g. 12/31/2024).
    /// </summary>
    public string EndDate { get; set; } = "";
}
