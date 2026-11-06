using DbClone.Application.DTOs;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

using DbAdapter.Definitions;

using Microsoft.Extensions.Logging;
using CanonicalModel.DTOs;

namespace DbClone.Application.Services;

/// <summary>
/// Provider-agnostic export orchestration.
/// Resolves the requested format by Id and delegates formatting.
/// </summary>
public sealed class ConnectionExportService : IConnectionExportService
{
    private readonly IReadOnlyList<IConnectionTextFormat> _formats;

    private readonly ILogger<ConnectionExportService> _logger;

    public ConnectionExportService(
        IEnumerable<IConnectionTextFormat> formats,
        ILogger<ConnectionExportService> logger)
    {
        _formats = formats.OrderBy(f => f.DisplayName).ToList();
        _logger = logger;
    }

    public string Export(DatabaseConnection connection, string formatId)
    {
        var fields = ToFields(connection);
        var format = _formats.FirstOrDefault(f =>
            f.Id.Equals(formatId, StringComparison.OrdinalIgnoreCase));

        if (format is null)
            throw new ArgumentException(
                $"No format registered with Id '{formatId}'.",
                nameof(formatId));

        if (!format.CanExport(fields))
            throw new InvalidOperationException(
                $"Format '{format.DisplayName}' cannot export connections for provider '{connection.Provider}'.");

        _logger.LogDebug("Exporting connection '{Name}' as {FormatId}", connection.Name, format.Id);
        return format.Export(fields);
    }

    public IReadOnlyList<ConnectionFormatInfo> GetSupportedFormats(DatabaseConnection connection)
    {
        var fields = ToFields(connection);
        return [
            .._formats
                .Where(f => f.CanExport(fields))
                .Select(f => new ConnectionFormatInfo(f.Id, f.DisplayName, f.TypicalSource))
        ];
    }

    /// <summary>
    /// Maps the application's connection model onto the engine's connection fields.
    /// </summary>
    private static ConnectionStringFields ToFields(DatabaseConnection connection) =>
        new(
            Host: connection.Host,
            Port: connection.Port,
            Database: connection.Database,
            Username: connection.Username,
            Password: connection.Password ?? string.Empty,
            SslMode: connection.SslMode,
            Provider: connection.Provider,
            Options: connection.Options);
}
