namespace DbAdapter.PostgreSql.Formats;

/// <summary>
/// Result of parsing a PostgreSQL connection URI. <see cref="Port"/> is null when the
/// authority carries none — choosing the engine default belongs to the caller.
/// </summary>
internal sealed record ParsedUri(
    string Scheme,
    string Host,
    int? Port,
    string Database,
    string? Username,
    string? Password,
    Dictionary<string, string> QueryParams);
