using System.Text.Json;

using DbClone.UI.Models;

namespace DbClone.UI.Services;

internal sealed class JsonReportService : IReportGenerationService
{
    private static readonly HashSet<string> SupportedFormats =
        new(StringComparer.OrdinalIgnoreCase) { ".json" };

    public bool CanGenerate(string formatIdentifier) => SupportedFormats.Contains(formatIdentifier);

    public string Generate(ComparisonReportData data)
    {
        var report = new
                         {
                             generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                             source = data.SourceSummary,
                             destination = data.DestSummary,
                             summary = new
                                           {
                                               identical = data.TotalIdentical,
                                               notices = data.TotalNotices,
                                               different = data.TotalDifferent,
                                               missingSource = data.TotalMissingSource,
                                               missingDest = data.TotalMissingDest,
                                               skipped = data.TotalSkipped,
                                               errors = data.TotalErrors,
                                               duration = data.Duration
                                           },
                             results = data.Items.Select(i => new
                                                                  {
                                                                      type = i.ObjectTypeDisplay,
                                                                      name = i.TableName,
                                                                      status = i.StatusText,
                                                                      sourceCount = i.SourceCount < 0 ? (long?)null : i.SourceCount,
                                                                      destCount = i.DestCount < 0 ? (long?)null : i.DestCount,
                                                                      details = i.Details
                                                                  })
                         };
        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    }
}
