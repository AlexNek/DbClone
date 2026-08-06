using System.Collections.Immutable;

using Microsoft.Extensions.Logging;

namespace DbClone.Application.Platforms;

/// <summary>
/// Resolves the applicable platform schemas for a connection based on
/// engine, host, and server version. Uses definitions loaded by
/// <see cref="PlatformDefinitionLoader"/>.
/// </summary>
public sealed class PlatformSchemaResolver
{
    private readonly PlatformDefinitionLoader _loader;
    private readonly ILogger<PlatformSchemaResolver> _logger;

    /// <summary>
    /// The database engine this resolver instance is scoped to (e.g. "postgresql").
    /// Set by the provider's DI registration — callers use parameterless convenience methods.
    /// </summary>
    public string Engine { get; }

    public PlatformSchemaResolver(PlatformDefinitionLoader loader, ILogger<PlatformSchemaResolver> logger, string engine = "postgresql")
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Engine = engine;
    }

    /// <summary>
    /// Resolves the platform definition for the given connection parameters.
    /// </summary>
    /// <param name="engine">Database engine (e.g. "postgresql").</param>
    /// <param name="host">Connection host (e.g. "myproject.supabase.co").</param>
    /// <param name="serverVersion">Server version string (e.g. "15.4").</param>
    /// <returns>
    /// A <see cref="PlatformResolution"/> with system/platform schemas.
    /// Never null — returns <see cref="PlatformResolution.None"/> if no definitions exist.
    /// </returns>
    public PlatformResolution Resolve(string engine, string host, string serverVersion)
    {
        var allDefs = _loader.GetAll()
            .Where(d => string.Equals(d.Engine, engine, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (allDefs.Count == 0)
            return PlatformResolution.None;

        // Find matching platform or fall back to base engine file
        var matched = FindMatchingPlatform(allDefs, host)
                      ?? allDefs.FirstOrDefault(d => d.Detection is null);

        if (matched is null)
            return PlatformResolution.None;

        var version = VersionRangeParser.ParseServerVersion(serverVersion);

        // Pick the ONE matching version entry (ranges must not overlap)
        var entry = matched.Versions
            .FirstOrDefault(v => VersionRangeParser.Satisfies(version, v.VersionRange));

        if (entry is null)
        {
            // Unsupported version — warn and fall back to base engine system schemas
            _logger.LogWarning(
                "Unsupported server version {Version} for platform '{Platform}'. Update definition file '{File}'.",
                serverVersion, matched.DisplayName, matched.SourceFileName);

            var baseEngine = allDefs.FirstOrDefault(d => d.Detection is null);
            var baseEntry = baseEngine?.Versions
                .FirstOrDefault(v => VersionRangeParser.Satisfies(version, v.VersionRange));

            return new PlatformResolution(
                DetectedPlatform: matched.DisplayName,
                SystemSchemas: baseEntry is not null
                    ? ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, baseEntry.SystemSchemas)
                    : ImmutableHashSet<string>.Empty,
                PlatformSchemas: ImmutableHashSet<string>.Empty,
                PlatformExtensions: ImmutableHashSet<string>.Empty,
                VersionWarning:
                    $"Unsupported server version {serverVersion} for platform '{matched.DisplayName}'. " +
                    $"Update definition file '{matched.SourceFileName}'.");
        }

        if (matched.Detection is not null)
        {
            _logger.LogInformation(
                "Detected platform: {Platform} (host={Host}, version={Version})",
                matched.DisplayName, host, serverVersion);
        }

        return new PlatformResolution(
            DetectedPlatform: matched.Detection is not null ? matched.DisplayName : null,
            SystemSchemas: ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, entry.SystemSchemas),
            PlatformSchemas: ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, entry.PlatformSchemas),
            PlatformExtensions: ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, entry.PlatformExtensions));
    }

    /// <summary>
    /// Detects the platform stable id from a host using the loaded
    /// <c>detection.hostPatterns</c> in .platform files.
    /// Returns null when no platform matches (base engine).
    /// </summary>
    public string? DetectPlatformId(string host) => DetectPlatformId(Engine, host);

    /// <summary>
    /// Returns the default connection settings (port, SSL mode) for a platform
    /// identified by its stable id. Falls back to the base engine file defaults,
    /// then to 5432/Prefer when no definition matches.
    /// </summary>
    public PlatformDefaults GetConnectionDefaults(string? platformId) => GetConnectionDefaults(Engine, platformId);

    /// <summary>
    /// Returns all platforms as (id, displayName) pairs, ordered with the base engine
    /// file first, then detected platforms alphabetically by displayName.
    /// Used to populate the connection type dropdown dynamically.
    /// </summary>
    public IReadOnlyList<PlatformEntry> GetAllPlatforms() => GetAllPlatforms(Engine);

    /// <summary>
    /// Detects the platform stable id from a host for a specific engine.
    /// </summary>
    public string? DetectPlatformId(string engine, string host)
    {
        if (string.IsNullOrEmpty(host))
            return null;

        var allDefs = _loader.GetAll()
            .Where(d => string.Equals(d.Engine, engine, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var matched = FindMatchingPlatform(allDefs, host);
        return matched?.StableId;
    }

    /// <summary>
    /// Returns the default connection settings (port, SSL mode) for a platform
    /// identified by its stable id. Falls back to the base engine file defaults,
    /// then to 5432/Prefer when no definition matches.
    /// </summary>
    public PlatformDefaults GetConnectionDefaults(string engine, string? platformId)
    {
        var allDefs = _loader.GetAll()
            .Where(d => string.Equals(d.Engine, engine, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Find by stable id, or fall back to base engine file (Detection is null)
        var def = platformId is not null
            ? allDefs.FirstOrDefault(d =>
                string.Equals(d.StableId, platformId, StringComparison.OrdinalIgnoreCase))
            : null;

        def ??= allDefs.FirstOrDefault(d => d.Detection is null);

        return def?.Defaults ?? new PlatformDefaults(Port: 5432, SslMode: "Prefer");
    }

    /// <summary>
    /// Returns all platforms for the given engine as (id, displayName) pairs, ordered
    /// with the base engine file first, then detected platforms alphabetically by displayName.
    /// Used to populate the connection type dropdown dynamically.
    /// </summary>
    public IReadOnlyList<PlatformEntry> GetAllPlatforms(string engine)
    {
        var allDefs = _loader.GetAll()
            .Where(d => string.Equals(d.Engine, engine, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Base engine file first (Detection is null), then platforms alphabetically
        var baseEngine = allDefs
            .Where(d => d.Detection is null)
            .Select(d => new PlatformEntry(d.StableId, d.DisplayName));

        var platforms = allDefs
            .Where(d => d.Detection is not null)
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(d => new PlatformEntry(d.StableId, d.DisplayName));

        return baseEngine.Concat(platforms).ToList();
    }

    private static PlatformDefinition? FindMatchingPlatform(
        IReadOnlyList<PlatformDefinition> defs, string host)
    {
        foreach (var def in defs)
        {
            if (def.Detection?.HostPatterns is { Count: > 0 } patterns
                && patterns.Any(p => MatchesHostPattern(host, p)))
                return def;
        }

        return null;
    }

    private static bool MatchesHostPattern(string host, string pattern) =>
        pattern.StartsWith('*')
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
}
