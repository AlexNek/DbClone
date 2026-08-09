namespace DbClone.PostgreSql.Formats;

/// <summary>Result of parsing a PostgreSQL connection URI.</summary>
internal sealed record ParsedUri(
    string Scheme,
    string Host,
    int Port,
    string Database,
    string? Username,
    string? Password,
    Dictionary<string, string> QueryParams);
