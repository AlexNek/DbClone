using System.Text.Json.Serialization;

namespace DbClone.Application.Platforms;

/// <summary>
/// Deserialization model for a .platform definition file.
/// </summary>
/// <param name="Id">
/// Stable machine identifier (e.g. "supabase", "postgresql"). Used as the persistence key
/// in saved connections. Never changes, even if displayName is localized.
/// Falls back to displayName (lowercased) when absent for backward compatibility.
/// </param>
/// <param name="Engine">Database engine identifier (e.g. "postgresql", "mysql").</param>
/// <param name="DisplayName">Human-readable platform name (e.g. "Supabase"). Localizable.</param>
/// <param name="FormatVersion">File format version for forward compatibility.</param>
/// <param name="Detection">
/// Detection rules. Null/absent = base engine file (fallback when no platform matches).
/// </param>
/// <param name="Defaults">
/// Default connection settings (port, SSL mode) for this platform.
/// Null/absent = use engine fallback (5432, Prefer).
/// </param>
/// <param name="Versions">Version-range entries. The resolver picks exactly one.</param>
/// <param name="SourceFileName">
/// Populated by the loader at runtime — the file name this definition was read from.
/// Not serialized from JSON.
/// </param>
public sealed record PlatformDefinition(
    [property: JsonPropertyName("id")]
    string? Id,
    [property: JsonPropertyName("engine")]
    string Engine,
    [property: JsonPropertyName("displayName")]
    string DisplayName,
    [property: JsonPropertyName("formatVersion")]
    string FormatVersion,
    [property: JsonPropertyName("detection")]
    PlatformDetection? Detection,
    [property: JsonPropertyName("defaults")]
    PlatformDefaults? Defaults,
    [property: JsonPropertyName("versions")]
    IReadOnlyList<VersionEntry> Versions,
    [property: JsonIgnore]
    string SourceFileName = "")
{
    /// <summary>
    /// The stable platform identifier. Falls back to lowercased displayName
    /// when the "id" field is absent (backward compatibility with older files).
    /// </summary>
    [JsonIgnore]
    public string StableId => Id ?? DisplayName.ToLowerInvariant();
}

/// <summary>
/// Detection rules for identifying a hosting platform from the connection host.
/// </summary>
/// <param name="HostPatterns">
/// Glob-style host patterns (e.g. "*.supabase.co"). Matched case-insensitively.
/// </param>
public sealed record PlatformDetection(
    [property: JsonPropertyName("hostPatterns")]
    IReadOnlyList<string>? HostPatterns);

/// <summary>
/// Default connection settings for a platform.
/// Used by the UI to pre-fill port and SSL mode when a platform is detected or selected.
/// </summary>
/// <param name="Port">Default port number (e.g. 5432, 11521).</param>
/// <param name="SslMode">Default SSL mode: "Disable", "Prefer", or "Require".</param>
public sealed record PlatformDefaults(
    [property: JsonPropertyName("port")]
    int Port,
    [property: JsonPropertyName("sslMode")]
    string SslMode);

/// <summary>
/// Everything that applies to a specific version range.
/// Each entry is complete and self-contained — no merging across entries.
/// </summary>
/// <param name="VersionRange">Range expression: "*", "&gt;=15.0 &lt;16.0", etc.</param>
/// <param name="SystemSchemas">Engine-internal schemas (always excluded from content reads).</param>
/// <param name="PlatformSchemas">Provider-managed schemas (excluded when user setting enabled).</param>
/// <param name="PlatformExtensions">Provider-managed extensions.</param>
/// <param name="Notes">Optional documentation — not used at runtime.</param>
public sealed record VersionEntry(
    [property: JsonPropertyName("versionRange")]
    string VersionRange,
    [property: JsonPropertyName("systemSchemas")]
    IReadOnlyList<string> SystemSchemas,
    [property: JsonPropertyName("platformSchemas")]
    IReadOnlyList<string> PlatformSchemas,
    [property: JsonPropertyName("platformExtensions")]
    IReadOnlyList<string> PlatformExtensions,
    [property: JsonPropertyName("notes")]
    string? Notes = null);

/// <summary>
/// Lightweight (id, displayName) pair for populating UI dropdowns.
/// The <paramref name="Id"/> is the stable persistence key; <paramref name="DisplayName"/> is the localizable label.
/// </summary>
public sealed record PlatformEntry(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}
