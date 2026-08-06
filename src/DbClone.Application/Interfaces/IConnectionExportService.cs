using DbClone.Application.Models;

namespace DbClone.Application.Interfaces;

/// <summary>
/// Provider-agnostic export orchestration service.
/// Resolves the requested format and delegates formatting.
/// </summary>
public interface IConnectionExportService
{
    /// <summary>Exports a connection to the specified format Id.</summary>
    string Export(DatabaseConnection connection, string formatId);

    /// <summary>Returns all formats that can export the given connection.</summary>
    IReadOnlyList<IConnectionFormat> GetSupportedFormats(DatabaseConnection connection);
}
