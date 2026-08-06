using DbClone.Application.Interfaces;
using DbClone.Application.Models;

using Microsoft.Extensions.Logging;

namespace DbClone.Application.Services;

/// <summary>
/// Provider-agnostic export orchestration.
/// Resolves the requested format by Id and delegates formatting.
/// </summary>
public sealed class ConnectionExportService : IConnectionExportService
{
    private readonly IReadOnlyList<IConnectionFormat> _formats;

    private readonly ILogger<ConnectionExportService> _logger;

    public ConnectionExportService(
        IEnumerable<IConnectionFormat> formats,
        ILogger<ConnectionExportService> logger)
    {
        _formats = formats.OrderBy(f => f.DisplayName).ToList();
        _logger = logger;
    }

    public string Export(DatabaseConnection connection, string formatId)
    {
        var format = _formats.FirstOrDefault(f =>
            f.Id.Equals(formatId, StringComparison.OrdinalIgnoreCase));

        if (format is null)
            throw new ArgumentException(
                $"No format registered with Id '{formatId}'.",
                nameof(formatId));

        if (!format.CanExport(connection))
            throw new InvalidOperationException(
                $"Format '{format.DisplayName}' cannot export connections for provider '{connection.Provider}'.");

        _logger.LogDebug("Exporting connection '{Name}' as {FormatId}", connection.Name, format.Id);
        return format.Export(connection);
    }

    public IReadOnlyList<IConnectionFormat> GetSupportedFormats(DatabaseConnection connection) =>
        _formats.Where(f => f.CanExport(connection)).ToList();
}
