using DbClone.Application.DTOs;

namespace DbClone.Application.Interfaces;

/// <summary>
/// Provider-agnostic import orchestration service.
/// Iterates registered formats by priority and delegates parsing to the first match.
/// </summary>
public interface IConnectionImportService
{
    /// <summary>Detects the format without fully parsing.</summary>
    DetectionResult Detect(string text);

    /// <summary>Returns all registered formats (for UI display).</summary>
    IReadOnlyList<IConnectionFormat> GetAllFormats();

    /// <summary>Imports a raw connection string, auto-detecting the format.</summary>
    ImportResult Import(string text);
}
