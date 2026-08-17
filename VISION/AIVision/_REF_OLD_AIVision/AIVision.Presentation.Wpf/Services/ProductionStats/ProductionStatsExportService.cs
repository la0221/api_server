using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Configuration;
using AIVision.Application.Contracts.ProductionStats;
using AIVision.Presentation.Wpf.Utilities;
using ClosedXML.Excel;

namespace AIVision.Presentation.Wpf.Services.ProductionStats;

public sealed class ProductionStatsExportService : IProductionStatsExportService
{
    public async Task<string> ExportCsvAsync(IEnumerable<WorkOrderStatsDto> stats, ProductionStatsUiConfig config, CancellationToken cancellationToken)
    {
        var materialized = Materialize(stats);
        if (materialized.Count == 0)
        {
            throw new InvalidOperationException("No statistics provided for CSV export.");
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = materialized.Count == 1
            ? $"{SanitizeFileName(materialized[0].Order.Code)}-stats-{timestamp}.csv"
            : $"ProductionStats-{timestamp}.csv";
        var path = Path.Combine(GetExportFolder(), fileName);

        var builder = new StringBuilder();
        foreach (var stat in materialized)
        {
            cancellationToken.ThrowIfCancellationRequested();

            builder.AppendLine($"Order,{Escape(stat.Order.Code)}");
            builder.AppendLine("Label,Value");
            foreach (var field in config.SummaryFields)
            {
                var value = ResolveValue(stat, field.Path);
                var formatted = ObjectPathResolver.Format(value, field.Format);
                builder.AppendLine($"{Escape(field.Label)},{Escape(formatted)}");
            }

            builder.AppendLine();
            builder.AppendLine("Defect,Count,Ratio");
            var total = Math.Max(1, stat.Total);

            // 使用動態瑕疵類型：優先從實際資料取得，確保匯出與面板顯示一致
            // 合併配置的 DefectBuckets 和實際資料中的瑕疵類型
            var allDefectTypes = new HashSet<string>(config.DefectBuckets, StringComparer.OrdinalIgnoreCase);
            foreach (var defectType in stat.Defects.Keys)
            {
                allDefectTypes.Add(defectType);
            }

            foreach (var defectType in allDefectTypes)
            {
                stat.Defects.TryGetValue(defectType, out var count);
                var label = config.DefectLabelMap.TryGetValue(defectType, out var lbl) ? lbl : defectType;
                var ratio = (double)count / total;
                builder.AppendLine($"{Escape(label)},{count},{ratio.ToString("P2", CultureInfo.InvariantCulture)}");
            }

            builder.AppendLine();
        }

        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public Task<string> ExportExcelAsync(IEnumerable<WorkOrderStatsDto> stats, ProductionStatsUiConfig config, CancellationToken cancellationToken)
    {
        var materialized = Materialize(stats);
        if (materialized.Count == 0)
        {
            throw new InvalidOperationException("No statistics provided for Excel export.");
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = materialized.Count == 1
            ? $"{SanitizeFileName(materialized[0].Order.Code)}-stats-{timestamp}.xlsx"
            : $"ProductionStats-{timestamp}.xlsx";
        var path = Path.Combine(GetExportFolder(), fileName);

        using var workbook = new XLWorkbook();
        foreach (var stat in materialized)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var worksheet = workbook.Worksheets.Add(SanitizeWorksheetName(stat.Order.Code));
            var row = 1;

            worksheet.Cell(row, 1).Value = "Label";
            worksheet.Cell(row, 2).Value = "Value";
            worksheet.Range(row, 1, row, 2).Style.Font.SetBold();
            row++;

            foreach (var field in config.SummaryFields)
            {
                var value = ResolveValue(stat, field.Path);
                var formatted = ObjectPathResolver.Format(value, field.Format);
                worksheet.Cell(row, 1).Value = field.Label;
                worksheet.Cell(row, 2).Value = formatted;
                row++;
            }

            row += 1;
            worksheet.Cell(row, 1).Value = "Defect";
            worksheet.Cell(row, 2).Value = "Count";
            worksheet.Cell(row, 3).Value = "Ratio";
            worksheet.Range(row, 1, row, 3).Style.Font.SetBold();
            row++;

            var total = Math.Max(1, stat.Total);

            // 使用動態瑕疵類型：優先從實際資料取得，確保匯出與面板顯示一致
            // 合併配置的 DefectBuckets 和實際資料中的瑕疵類型
            var allDefectTypes = new HashSet<string>(config.DefectBuckets, StringComparer.OrdinalIgnoreCase);
            foreach (var defectType in stat.Defects.Keys)
            {
                allDefectTypes.Add(defectType);
            }

            foreach (var defectType in allDefectTypes)
            {
                stat.Defects.TryGetValue(defectType, out var count);
                var label = config.DefectLabelMap.TryGetValue(defectType, out var lbl) ? lbl : defectType;
                var ratio = (double)count / total;

                worksheet.Cell(row, 1).Value = label;
                worksheet.Cell(row, 2).Value = count;
                worksheet.Cell(row, 3).Value = ratio;
                worksheet.Cell(row, 3).Style.NumberFormat.Format = "0.00%";
                row++;
            }

            worksheet.Columns().AdjustToContents();
        }

        workbook.SaveAs(path);
        return Task.FromResult(path);
    }

    private static List<WorkOrderStatsDto> Materialize(IEnumerable<WorkOrderStatsDto> stats) =>
        stats switch
        {
            List<WorkOrderStatsDto> list => list,
            null => new List<WorkOrderStatsDto>(),
            _ => stats.Where(s => s is not null).ToList()!
        };

    private static object? ResolveValue(WorkOrderStatsDto stat, string path)
    {
        var value = ObjectPathResolver.Resolve(stat, path);
        return value ?? ObjectPathResolver.Resolve(stat.Order, path);
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n');
        var escaped = value.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }

    private static string GetExportFolder()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AIVision", "Exports");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string SanitizeFileName(string code)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(code.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string SanitizeWorksheetName(string code)
    {
        var sanitized = SanitizeFileName(code);
        if (sanitized.Length > 31)
        {
            sanitized = sanitized[..31];
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "Order" : sanitized;
    }
}
