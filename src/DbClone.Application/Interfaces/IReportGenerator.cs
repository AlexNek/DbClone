using DbClone.Application.DTOs;

namespace DbClone.Application.Interfaces;

/// <summary>
/// Generates reports from copy results.
/// </summary>
public interface IReportGenerator
{
    /// <summary>
    /// Generates an HTML report.
    /// </summary>
    Task<string> GenerateHtmlAsync(
        CopyResult result,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a JSON report.
    /// </summary>
    Task<string> GenerateJsonAsync(
        CopyResult result,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a Markdown report.
    /// </summary>
    Task<string> GenerateMarkdownAsync(
        CopyResult result,
        CancellationToken cancellationToken = default);
}
