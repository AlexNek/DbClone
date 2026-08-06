using DbClone.UI.Models;

namespace DbClone.UI.Services;

public interface IReportGenerationService
{
    /// <summary>
    /// Returns true if this service can generate a report for the given file extension (e.g. ".html", ".json").
    /// </summary>
    bool CanGenerate(string fileExtension);

    string Generate(ComparisonReportData data);
}
