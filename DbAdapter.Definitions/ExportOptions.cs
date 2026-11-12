using CanonicalModel.Interchange;

namespace DbAdapter.Definitions;

/// <summary>
/// Options for export operations.
/// </summary>
public sealed record ExportOptions
{
    /// <summary>Gets or sets the requested snapshot consistency mode.</summary>
    public ESnapshotConsistency RequestedConsistency { get; init; } = ESnapshotConsistency.ConsistentSnapshot;

    /// <summary>Gets or sets the chunk size in bytes.</summary>
    public int ChunkBytes { get; init; } = 32 * 1024 * 1024; // 32 MiB

    /// <summary>Gets or sets the maximum rows per chunk.</summary>
    public int MaxRowsPerChunk { get; init; } = 100_000;

    /// <summary>Gets or sets whether to include data.</summary>
    public bool IncludeData { get; init; } = true;

    /// <summary>Gets or sets the tables to include (empty = all, subject to <see cref="ExcludedTables"/>).</summary>
    public IReadOnlyList<TableRef> Tables { get; init; } = [];

    /// <summary>Gets or sets the tables to exclude from the export. Applied after <see cref="Tables"/>.</summary>
    public IReadOnlySet<TableRef> ExcludedTables { get; init; } = new HashSet<TableRef>();

    /// <summary>Gets or sets the default row order mode.</summary>
    public ERowOrder DefaultRowOrder { get; init; } = ERowOrder.PrimaryKey;

    /// <summary>
    /// Gets or sets whether the writer also produces <c>summary.md</c> in the catalog root. The report is
    /// written after <c>manifest.json</c> closes, so turning it off changes nothing about the catalog
    /// itself, and a catalog without it is as restorable as one with it.
    /// </summary>
    public bool IncludeCatalogReport { get; init; } = true;

    /// <summary>
    /// Gets or sets whether the writer also produces the catalog's own SQL scripts (<c>sql/schema.sql</c>
    /// and <c>sql/data.sql</c>) beside it. Off by default: the pass is asked for, never assumed, so an
    /// export that builds default options writes exactly the catalog it wrote before the pass existed.
    /// </summary>
    public bool IncludeSqlScripts { get; init; }

    /// <summary>
    /// Gets or sets the largest a data script file may become, in bytes, before the pass opens the next one.
    /// <c>0</c> — the default — writes the single <c>sql/data.sql</c> the pass wrote before this number
    /// existed, so a consumer that builds default options gets today's layout (FR13).
    /// </summary>
    public long SqlScriptMaxDataFileBytes { get; init; }
}

/// <summary>
/// Options for import operations.
/// </summary>
public sealed record ImportOptions
{
    /// <summary>Gets or sets the policy for unsupported features.</summary>
    public EUnsupportedFeaturePolicy UnsupportedFeaturePolicy { get; init; } = EUnsupportedFeaturePolicy.Fail;

    /// <summary>Gets or sets the policy for suspected secrets.</summary>
    public EFavorSecretPolicy SecretPolicy { get; init; } = EFavorSecretPolicy.Omit;

    /// <summary>Gets or sets the tables to import (empty = all).</summary>
    public IReadOnlyList<TableRef> Tables { get; init; } = [];

    /// <summary>Gets or sets whether to validate the target after import.</summary>
    public bool ValidateAfterImport { get; init; } = true;
}
