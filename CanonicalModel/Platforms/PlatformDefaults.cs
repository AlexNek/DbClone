using System.Text.Json.Serialization;
using CanonicalModel.Platforms;

namespace CanonicalModel.Platforms;

/// <summary>
/// Default connection settings for a platform.
/// Used by the UI to pre-fill port, SSL mode and username when a platform is detected or selected.
/// </summary>
/// <param name="Port">Default port number (e.g. 5432, 11521).</param>
/// <param name="SslMode">Default SSL mode: "Disable", "Prefer", or "Require".</param>
/// <param name="Username">Default login user supplied by the engine's platform data.</param>
public sealed record PlatformDefaults(
    [property: JsonPropertyName("port")]
    int Port,
    [property: JsonPropertyName("sslMode")]
    string SslMode,
    [property: JsonPropertyName("username")]
    string Username);
