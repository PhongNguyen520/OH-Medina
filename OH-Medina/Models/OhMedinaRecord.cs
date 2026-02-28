using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using CsvHelper.TypeConversion;

namespace OH_Medina.Models;

/// <summary>
/// Flat record for Medina County CSV export. Multiple values are concatenated with semicolons (;).
/// Pipe-delimited CSV output. Field definitions follow the Website Field Label / Entry spec.
/// </summary>
public class OhMedinaRecord
{
    // --- Single (Image 1) ---
    [Name("Document No")]
    public string DocumentNo { get; set; } = "";

    [Name("Recorded Date")]
    public string RecordedDate { get; set; } = "";

    [Name("Document Type")]
    public string DocumentType { get; set; } = "";

    [Name("Consideration")]
    public string Consideration { get; set; } = "";

    // --- Multiple: Party 1, Party 2, Associated Documents, Legals (Image 2) ---
    [Name("Party 1")]
    [TypeConverter(typeof(SemicolonListConverter))]
    public List<string> Party1 { get; set; } = new();

    [Name("Party 2")]
    [TypeConverter(typeof(SemicolonListConverter))]
    public List<string> Party2 { get; set; } = new();

    [Name("Associated Documents")]
    [TypeConverter(typeof(SemicolonListConverter))]
    public List<string> AssociatedDocuments { get; set; } = new();

    [Name("Notes")]
    public string Notes { get; set; } = "";

    [Name("Legals")]
    [TypeConverter(typeof(SemicolonListConverter))]
    public List<string> Legals { get; set; } = new();

    [Name("PDF URL")]
    public string PdfUrl { get; set; } = "";
}

/// <summary>
/// Converts <see cref="List{T}"/> of string to/from a single CSV cell value using semicolon as delimiter.
/// </summary>
public class SemicolonListConverter : DefaultTypeConverter
{
    private const string Delimiter = ";";

    public override string ConvertToString(object? value, IWriterRow row, MemberMapData memberMapData)
    {
        if (value is null)
            return string.Empty;

        if (value is List<string> list)
            return string.Join(Delimiter, list);

        return value.ToString() ?? string.Empty;
    }

    public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text
            .Split(Delimiter, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToList();
    }
}
