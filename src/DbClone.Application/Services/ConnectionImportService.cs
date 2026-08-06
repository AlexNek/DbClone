using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Exceptions;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

using Microsoft.Extensions.Logging;

namespace DbClone.Application.Services;

/// <summary>
/// Provider-agnostic import orchestration.
/// Iterates all registered <see cref="IConnectionFormat"/> instances by priority;
/// the first format whose CanImport returns true wins.
/// </summary>
public sealed class ConnectionImportService : IConnectionImportService
{
    private readonly IReadOnlyList<IConnectionFormat> _formats;

    private readonly ILogger<ConnectionImportService> _logger;

    public ConnectionImportService(
        IEnumerable<IConnectionFormat> formats,
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

    public IReadOnlyList<IConnectionFormat> GetAllFormats() => _formats;

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
                var connection = format.Parse(trimmed);
                var warnings = CollectWarnings(connection);

                return new ImportResult
                           {
                               Success = true,
                               Connection = connection,
                               Warnings = warnings,
                               DetectedFormatName = format.DisplayName,
                               DetectedProvider = format.Provider.ToString(),
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

        // Remove internal markers from Options so they don't leak into the connection model
        var invalidMarkers = connection.Options.Keys
            .Where(k => k.StartsWith("_invalid:", StringComparison.Ordinal)).ToList();
        foreach (var key in invalidMarkers)
            connection.Options.Remove(key);

        return warnings;
    }
}
