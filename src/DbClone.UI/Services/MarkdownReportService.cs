using System.Text;

using DbClone.UI.Models;

namespace DbClone.UI.Services;

internal sealed class MarkdownReportService : IReportGenerationService
{
    private static readonly HashSet<string> SupportedFormats =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown" };

    public bool CanGenerate(string formatIdentifier) => SupportedFormats.Contains(formatIdentifier);

    public string Generate(ComparisonReportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Database Comparison Report");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"**Source:** {data.SourceSummary}");
        sb.AppendLine($"**Destination:** {data.DestSummary}");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- **Identical:** {data.TotalIdentical}");
        sb.AppendLine($"- **Notices:** {data.TotalNotices}");
        sb.AppendLine($"- **Different:** {data.TotalDifferent}");
        sb.AppendLine($"- **Missing in Source:** {data.TotalMissingSource}");
        sb.AppendLine($"- **Missing in Destination:** {data.TotalMissingDest}");
        sb.AppendLine($"- **Skipped:** {data.TotalSkipped}");
        sb.AppendLine($"- **Errors:** {data.TotalErrors}");
        sb.AppendLine($"- **Duration:** {data.Duration}");
        sb.AppendLine();
        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("| Type | Object | Status | Source | Dest | Details |");
        sb.AppendLine("|------|--------|--------|--------|------|---------|");
        foreach (var item in data.Items)
        {
            var src = item.SourceCount < 0 ? "N/A" : item.SourceCount.ToString("N0");
            var dst = item.DestCount < 0 ? "N/A" : item.DestCount.ToString("N0");
            sb.AppendLine(
                $"| {item.ObjectTypeDisplay} | {item.TableName} | {item.StatusText} | {src} | {dst} | {item.Details} |");
        }

        return sb.ToString();
    }
}
