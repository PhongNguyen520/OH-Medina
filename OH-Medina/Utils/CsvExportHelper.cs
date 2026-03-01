using System.Collections.Generic;
using System.IO;
using CsvHelper;
using CsvHelper.Configuration;
using OH_Medina.Models;
using OH_Medina.Services;

namespace OH_Medina.Utils;

/// <summary>CSV export and Apify Key-Value Store upload for Medina County records.</summary>
public static class CsvExportHelper
{
    /// <summary>Export records to pipe-delimited CSV and upload to Apify Key-Value Store.</summary>
    public static async Task ExportToCsvAndUploadAsync(List<OhMedinaRecord> records, string dateKey)
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

            var config = new CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
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
}
