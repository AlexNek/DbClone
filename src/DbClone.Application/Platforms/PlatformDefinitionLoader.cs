using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace DbClone.Application.Platforms;

/// <summary>
/// Reads .platform definition files from disk and caches them.
/// Corrupt or invalid files are skipped with a warning — they never prevent
/// other definitions from loading.
/// </summary>
public sealed class PlatformDefinitionLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _platformsDirectory;
    private readonly ILogger<PlatformDefinitionLoader> _logger;
    private IReadOnlyList<PlatformDefinition>? _cache;

    public PlatformDefinitionLoader(string platformsDirectory, ILogger<PlatformDefinitionLoader> logger)
    {
        _platformsDirectory = platformsDirectory ?? throw new ArgumentNullException(nameof(platformsDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns all loaded platform definitions (cached after first call).
    /// </summary>
    public IReadOnlyList<PlatformDefinition> GetAll()
        => _cache ??= LoadFromDisk();

    /// <summary>
    /// Invalidates the cache — next call to <see cref="GetAll"/> re-reads from disk.
    /// </summary>
    public void Reload() => _cache = null;

    private IReadOnlyList<PlatformDefinition> LoadFromDisk()
    {
        if (!Directory.Exists(_platformsDirectory))
        {
            _logger.LogDebug(
                "Platforms directory not found: {Dir} — using hardcoded fallback",
                _platformsDirectory);
            return [];
        }

        var files = Directory.GetFiles(_platformsDirectory, "*.platform");
        var definitions = new List<PlatformDefinition>(files.Length);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            try
            {
                var json = File.ReadAllText(file);
                var def = JsonSerializer.Deserialize<PlatformDefinition>(json, JsonOptions);
                if (def is not null)
                {
                    ValidateNoOverlap(def, fileName);
                    definitions.Add(def with { SourceFileName = fileName });
                    _logger.LogDebug(
                        "Loaded platform definition: {File} (engine={Engine}, platform={Platform})",
                        fileName, def.Engine, def.DisplayName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Skipping invalid platform definition file '{File}'",
                    fileName);
            }
        }

        _logger.LogInformation(
            "Loaded {Count} platform definition(s) from {Dir}",
            definitions.Count, _platformsDirectory);

        return definitions;
    }

    /// <summary>
    /// Validates that no two version entries within a single definition file have
    /// overlapping ranges. Overlap is always a file authoring mistake.
    /// Logs an error for each overlapping pair; first match wins at resolution time.
    /// </summary>
    private void ValidateNoOverlap(PlatformDefinition def, string fileName)
    {
        var entries = def.Versions;
        for (var i = 0; i < entries.Count; i++)
        {
            for (var j = i + 1; j < entries.Count; j++)
            {
                if (VersionRangeParser.RangesOverlap(entries[i].VersionRange, entries[j].VersionRange))
                {
                    _logger.LogError(
                        "Overlapping version ranges in '{File}': '{Range1}' and '{Range2}'. "
                        + "This is a file authoring mistake — ranges must not overlap. First match wins.",
                        fileName,
                        entries[i].VersionRange,
                        entries[j].VersionRange);
                }
            }
        }
    }
}
