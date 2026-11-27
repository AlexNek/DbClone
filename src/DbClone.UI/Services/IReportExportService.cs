using DbClone.UI.Models;

namespace DbClone.UI.Services;

/// <summary>
/// Renders a comparison report and puts it where the user asked for it. The view model chooses the
/// format and the path; rendering and the file write are this service's job, so no view model opens
/// a file.
/// </summary>
public interface IReportExportService
{
    /// <summary>Generates a report in the requested format.</summary>
    /// <param name="formatIdentifier">Format name, MIME type, or file extension (e.g. "html", ".md").</param>
    /// <param name="data">Comparison data to render.</param>
    string Export(string formatIdentifier, ComparisonReportData data);

    /// <summary>
    /// Renders a report for the path's extension and writes it there, replacing any file already
    /// at that path.
    /// </summary>
    void ExportToFile(string path, ComparisonReportData data);
}
