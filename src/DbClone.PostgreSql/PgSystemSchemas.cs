namespace DbClone.PostgreSql;

/// <summary>
/// Universal PostgreSQL system schemas that are ALWAYS excluded from user-facing
/// operations for performance reasons (they contain thousands of internal objects).
/// Platform-specific schemas (Supabase auth/storage, Aiven admin, etc.) are NOT
/// hardcoded here — they are excluded via .platform definition files and the
/// ownership heuristic in <see cref="Metadata.PgMetadataReader"/>.
/// This class serves as a hardcoded fallback when no .platform files are available.
/// </summary>
public static class PgSystemSchemas
{
    /// <summary>
    /// Core PostgreSQL system schemas — always excluded from metadata queries.
    /// </summary>
    public static readonly string[] All =
            ["pg_catalog", "information_schema", "pg_toast"];

    /// <summary>
    /// Immutable set version of <see cref="All"/> for efficient lookups.
    /// </summary>
    public static readonly IReadOnlySet<string> AllSet =
        new HashSet<string>(All, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pre-formatted SQL IN-clause list for direct embedding in queries.
    /// Produces: 'pg_catalog', 'information_schema', 'pg_toast'
    /// </summary>
    public static readonly string SqlList =
        string.Join(", ", All.Select(s => $"'{s}'"));

    /// <summary>
    /// Pre-formatted SQL IN-clause list including 'public' — used by maintenance
    /// operations that enumerate only non-user schemas.
    /// </summary>
    public static readonly string SqlListWithPublic =
        string.Join(", ", [.. All.Select(s => $"'{s}'"), "'public'"]);
}
