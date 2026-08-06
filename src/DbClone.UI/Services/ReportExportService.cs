using DbClone.UI.Models;

namespace DbClone.UI.Services;

/// <summary>
/// Orchestrator that routes export requests to the appropriate report generator
/// using the Self-Identifying Strategy pattern.
/// </summary>
public sealed class ReportExportService
{
    private readonly IEnumerable<IReportGenerationService> _generators;

    public ReportExportService(IEnumerable<IReportGenerationService> generators)
    {
        _generators = generators;
    }

    /// <summary>
    /// Generates a report in the requested format.
    /// </summary>
    /// <param name="formatIdentifier">Format name, MIME type, or file extension (e.g. "html", "application/json", ".md").</param>
    /// <param name="data">Comparison data to render.</param>
    /// <returns>The generated report content.</returns>
    /// <exception cref="NotSupportedException">Thrown when no registered generator supports the requested format.</exception>
    public string Export(string formatIdentifier, ComparisonReportData data)
    {
        var generator = _generators.FirstOrDefault(g => g.CanGenerate(formatIdentifier))
                        ?? throw new NotSupportedException(
                            $"Report format '{formatIdentifier}' is not supported.");

        return generator.Generate(data);
    }
}
