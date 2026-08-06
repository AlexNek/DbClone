using DbClone.Application.Models;

namespace DbClone.Application.DTOs;

/// <summary>
/// Result of a connection string import operation.
/// </summary>
public sealed class ImportResult
{
    /// <summary>Detection confidence from 0.0 to 1.0.</summary>
    public double Confidence { get; init; }

    public DatabaseConnection? Connection { get; init; }

    /// <summary>Display name of the detected format (e.g. "PostgreSQL URI").</summary>
    public string? DetectedFormatName { get; init; }

    /// <summary>Provider name (e.g. "PostgreSQL").</summary>
    public string? DetectedProvider { get; init; }

    public bool Success { get; init; }

    /// <summary>Typical source ecosystem (e.g. "Java", "Python", ".NET").</summary>
    public string? TypicalSource { get; init; }

    public IReadOnlyList<ImportWarning> Warnings { get; init; } = [];

    public static ImportResult Failed(IReadOnlyList<ImportWarning> warnings) =>
        new() { Success = false, Warnings = warnings };
}
