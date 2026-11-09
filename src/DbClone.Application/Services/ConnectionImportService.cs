using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Exceptions;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

using DbAdapter.Definitions;

using Microsoft.Extensions.Logging;
using CanonicalModel.Enums;
using CanonicalModel.DTOs;

namespace DbClone.Application.Services;

/// <summary>
/// Provider-agnostic import orchestration.
/// Iterates all registered <see cref="IConnectionTextFormat"/> instances by priority;
/// the first format whose CanImport returns true wins.
/// </summary>
public sealed class ConnectionImportService : IConnectionImportService
{
    /// <summary>The highest port a TCP server can listen on — a protocol limit, not an engine default.</summary>
    private const int MaxTcpPort = 65535;

    private readonly IReadOnlyList<IConnectionTextFormat> _formats;

    private readonly ILogger<ConnectionImportService> _logger;

    public ConnectionImportService(
        IEnumerable<IConnectionTextFormat> formats,
        ILogger<ConnectionImportService> logger)
    {
        _formats = formats.OrderBy(f => f.DetectionPriority).ToList();
        _logger = logger;
    }

    public DetectionResult Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return DetectionResult.None;

        var trimmed = text.Trim();

        foreach (var format in _formats)
        {
            if (!format.CanImport(trimmed))
                continue;

            return new DetectionResult(
                IsDetected: true,
                Provider: format.Provider,
                FormatId: format.Id,
                FormatDisplayName: format.DisplayName,
                Confidence: 1.0,
                TypicalSource: format.TypicalSource);
        }

        return DetectionResult.None;
    }

    public IReadOnlyList<ConnectionFormatInfo> GetAllFormats() =>
        [.._formats.Select(f => new ConnectionFormatInfo(f.Id, f.DisplayName, f.TypicalSource))];

    public ImportResult Import(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ImportResult.Failed(
                    [new ImportWarning(EWarningLevel.Error, "Input text is empty or whitespace.")]);
        }

        var trimmed = text.Trim();

        foreach (var format in _formats)
        {
            if (!format.CanImport(trimmed))
                continue;

            _logger.LogDebug("Detected format {FormatId} for input", format.Id);

            try
            {
                var connection = ToConnection(format.Parse(trimmed));
                var warnings = CollectWarnings(connection);

                return new ImportResult
                           {
                               Success = true,
                               Connection = connection,
                               Warnings = warnings,
                               DetectedFormatName = format.DisplayName,
                               DetectedProvider = format.Provider,
                               Confidence = 1.0,
                               TypicalSource = format.TypicalSource
                           };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Format {FormatId} claimed CanImport but Parse failed",
                    format.Id);
                return ImportResult.Failed(
                        [new ImportWarning(EWarningLevel.Error, $"Parse failed: {ex.Message}")]);
            }
        }

        throw new UnsupportedConnectionFormatException();
    }

    /// <summary>
    /// Maps the engine's connection fields onto the application's connection model.
    /// </summary>
    private static DatabaseConnection ToConnection(ConnectionStringFields fields)
    {
        var connection = new DatabaseConnection
                             {
                                 Provider = fields.Provider,
                                 Host = fields.Host,
                                 Port = fields.Port,
                                 Database = fields.Database,
                                 Username = fields.Username,
                                 Password = fields.Password,
                                 SslMode = fields.SslMode
                             };

        if (fields.Options is null)
            return connection;

        foreach (var (key, value) in fields.Options)
            connection.Options[key] = value;

        return connection;
    }

    private static List<ImportWarning> CollectWarnings(DatabaseConnection connection)
    {
        var warnings = new List<ImportWarning>();

        if (string.IsNullOrEmpty(connection.Password))
        {
            if (connection.Options.ContainsKey("SupabaseProjectRef"))
            {
                var projectRef = connection.Options["SupabaseProjectRef"];
                warnings.Add(
                    new ImportWarning(
                        EWarningLevel.Warning,
                        $"Password is missing. Find it in Supabase Dashboard → Settings → Database → Connection string (https://supabase.com/dashboard/project/{projectRef}/settings/database)",
                        "Password"));
            }
            else
            {
                warnings.Add(
                    new ImportWarning(EWarningLevel.Warning, "Password is missing.", "Password"));
            }
        }

        if (string.IsNullOrEmpty(connection.Database))
            warnings.Add(
                new ImportWarning(
                    EWarningLevel.Info,
                    "Database name is not specified.",
                    "Database"));

        if (connection.SslMode == ESslMode.Disable)
            warnings.Add(new ImportWarning(EWarningLevel.Info, "SSL is disabled.", "SslMode"));

        foreach (var option in connection.Options)
        {
            if (option.Key.StartsWith("_invalid:", StringComparison.Ordinal))
            {
                var invalidKey = option.Key["_invalid:".Length..];
                warnings.Add(
                    new ImportWarning(
                        EWarningLevel.Warning,
                        $"Unrecognized parameter ignored: {invalidKey}",
                        invalidKey));
            }
        }

        // Values the format had to take from the engine defaults because the text omitted
        // them (a Neon or Supabase string carries no Port key, for example)
        foreach (var option in connection.Options)
        {
            if (!option.Key.StartsWith("_defaulted:", StringComparison.Ordinal))
                continue;

            var defaultedField = option.Key["_defaulted:".Length..];
            warnings.Add(
                new ImportWarning(
                    EWarningLevel.Warning,
                    $"{defaultedField} is not specified; the default {option.Value} will be used.",
                    defaultedField));
        }

        // A port the text supplied but no server can listen on is kept as given and
        // reported — the user decides what to do with it, the import does not swallow it.
        if (connection.Port is < 1 or > MaxTcpPort)
            warnings.Add(
                new ImportWarning(
                    EWarningLevel.Warning,
                    $"Port {connection.Port} is outside the range 1-{MaxTcpPort}.",
                    "Port"));

        // Remove internal markers from Options so they don't leak into the connection model
        var internalMarkers = connection.Options.Keys
            .Where(k => k.StartsWith("_invalid:", StringComparison.Ordinal)
                        || k.StartsWith("_defaulted:", StringComparison.Ordinal)).ToList();
        foreach (var key in internalMarkers)
            connection.Options.Remove(key);

        return warnings;
    }
}
