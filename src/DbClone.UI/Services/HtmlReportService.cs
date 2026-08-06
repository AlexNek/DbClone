using System.Text;

using DbClone.Application.Enums;
using DbClone.UI.Models;

namespace DbClone.UI.Services;

internal sealed class HtmlReportService : IReportGenerationService
{
    private const string CssClassError = "status-error";

    private const string CssClassSuccess = "status-success";

    private const string CssClassWarning = "status-warning";

    private const string CssColorError = "#F44336";

    private const string CssColorSuccess = "#4CAF50";

    private const string CssColorWarning = "#FFC107";

    private static readonly HashSet<string> SupportedFormats =
        new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm" };

    private static readonly Dictionary<ECompareStatus, string> CssClassMap = new()
        {
            [ECompareStatus.Identical] = CssClassSuccess,
            [ECompareStatus.Notice] = CssClassSuccess,
            [ECompareStatus.Different] = CssClassWarning,
            [ECompareStatus.MissingSource] = CssClassError,
            [ECompareStatus.MissingDest] = CssClassError,
            [ECompareStatus.Skipped] = CssClassWarning,
            [ECompareStatus.Error] = CssClassError,
        };

    public bool CanGenerate(string formatIdentifier) => SupportedFormats.Contains(formatIdentifier);

    public string Generate(ComparisonReportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "<!DOCTYPE html><html><head><meta charset='utf-8'><title>Comparison Report</title>");
        sb.AppendLine(
            $"<style>body{{font-family:Segoe UI,sans-serif;margin:20px}}table{{border-collapse:collapse;width:100%}}th,td{{border:1px solid #ddd;padding:8px;text-align:left}}th{{background:#f5f5f5}}.{CssClassSuccess}{{color:{CssColorSuccess}}}.{CssClassWarning}{{color:{CssColorWarning}}}.{CssClassError}{{color:{CssColorError}}}</style></head><body>");
        sb.AppendLine("<h1>Database Comparison Report</h1>");
        sb.AppendLine($"<p>Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
        sb.AppendLine($"<p>Source: {data.SourceSummary}</p>");
        sb.AppendLine($"<p>Destination: {data.DestSummary}</p>");
        sb.AppendLine("<h2>Summary</h2>");
        sb.AppendLine(
            $"<p>Identical: {data.TotalIdentical} | Notices: {data.TotalNotices} | Different: {data.TotalDifferent} | Missing Src: {data.TotalMissingSource} | Missing Dst: {data.TotalMissingDest} | Skipped: {data.TotalSkipped} | Errors: {data.TotalErrors}</p>");
        sb.AppendLine($"<p>Duration: {data.Duration}</p>");
        sb.AppendLine(
            "<h2>Results</h2><table><tr><th>Type</th><th>Object</th><th>Status</th><th>Source</th><th>Dest</th><th>Details</th></tr>");
        foreach (var item in data.Items)
        {
            var srcDisplay = item.SourceCount < 0 ? "N/A" : item.SourceCount.ToString("N0");
            var dstDisplay = item.DestCount < 0 ? "N/A" : item.DestCount.ToString("N0");
            sb.AppendLine(
                $"<tr><td>{item.ObjectTypeDisplay}</td><td>{item.TableName}</td><td class='{CssClassMap[item.Status]}'>{item.StatusText}</td><td>{srcDisplay}</td><td>{dstDisplay}</td><td>{item.Details}</td></tr>");
        }

        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }
}
