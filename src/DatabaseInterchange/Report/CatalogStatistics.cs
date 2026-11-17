namespace DatabaseInterchange.Report;

/// <summary>
/// The render-ready account of one catalog: its identity, the counts of every captured kind, the three
/// byte measures, the per-table rows, the per-schema rollup and the size of every metadata file.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that makes the renderer testable without a database and without a filesystem
/// (D2): a test hands the builder an in-memory snapshot plus the writer's own numbers and asserts on
/// the text that comes out. It is <em>recomputed on every store and never persisted in this form</em> —
/// no reader, no version field, no consumer outside the renderer — so it must not grow into a third
/// interchange format. <c>catalog.json</c> and the section files stay the only authoritative structure.
/// </para>
/// <para>
/// The three byte measures (FR3) are composed exactly as D3 defines them and never conflated:
/// <see cref="DataBytes"/> is row chunks plus blob containers, <see cref="MetadataBytes"/> is every
/// other file of the catalog including the two root files, and <see cref="CatalogBytes"/> is the sum —
/// the size of the folder the user received, excluding <c>summary.md</c> itself.
/// </para>
/// </remarks>
internal sealed record CatalogStatistics
{
    // ---- identity (FR2) ----

    /// <summary>Gets the database name as captured.</summary>
    public required string DatabaseName { get; init; }

    /// <summary>Gets the producer string the catalog itself records.</summary>
    public required string Producer { get; init; }

    /// <summary>Gets the source dialect, as the writer was told it.</summary>
    public required string SourceDialect { get; init; }

    /// <summary>Gets the interchange format version of the catalog.</summary>
    public int FormatVersion { get; init; }

    /// <summary>Gets the sections the writer finished with, by name (§6 metadata preamble).</summary>
    public IReadOnlyList<string> SectionNames { get; init; } = [];

    /// <summary>
    /// Gets the export instant the writer stamped into <c>export.json</c>, or <see langword="null"/>
    /// when the catalog was never completed (D3).
    /// </summary>
    public DateTimeOffset? ExportedAt { get; init; }

    /// <summary>Gets the consistency mode actually used, spelled for reading (§6 header).</summary>
    public required string Consistency { get; init; }

    /// <summary>
    /// Gets how many tables the selection excluded. Printed in the scope line, because a count of
    /// captured objects must not read as a count of the source database (FR17).
    /// </summary>
    public int ExcludedTableCount { get; init; }

    // ---- counts of every captured kind (FR2) ----

    /// <summary>Gets the captured schemas.</summary>
    public int SchemaCount { get; init; }

    /// <summary>Gets the captured tables.</summary>
    public int TableCount { get; init; }

    /// <summary>Gets the captured columns.</summary>
    public int ColumnCount { get; init; }

    /// <summary>Gets the captured plain views.</summary>
    public int ViewCount { get; init; }

    /// <summary>Gets the captured materialized views.</summary>
    public int MaterializedViewCount { get; init; }

    /// <summary>Gets the captured routines.</summary>
    public int RoutineCount { get; init; }

    /// <summary>Gets the captured sequences.</summary>
    public int SequenceCount { get; init; }

    /// <summary>Gets the captured extensions.</summary>
    public int ExtensionCount { get; init; }

    /// <summary>Gets the captured foreign-key constraints, including those pointing outside.</summary>
    public int ForeignKeyCount { get; init; }

    /// <summary>Gets the captured <c>viewReference</c> relationships.</summary>
    public int ViewReferenceCount { get; init; }

    /// <summary>Gets the captured roles.</summary>
    public int RoleCount { get; init; }

    /// <summary>Gets the captured grants.</summary>
    public int GrantCount { get; init; }

    /// <summary>Gets the captured row-level-security policies.</summary>
    public int PolicyCount { get; init; }

    // ---- global data totals (FR3) ----

    /// <summary>
    /// Gets whether any table data was captured. A schema-only store is <see langword="false"/>, and the
    /// renderer prints <c>no data captured</c> rather than a table of zeros (FR10).
    /// </summary>
    public bool HasData { get; init; }

    /// <summary>Gets the rows written across every table of the catalog.</summary>
    public long Rows { get; init; }

    /// <summary>Gets the row-chunk file count. Files, not non-empty files (D3).</summary>
    public long ChunkFiles { get; init; }

    /// <summary>Gets the total bytes of the row-chunk files.</summary>
    public long RowBytes { get; init; }

    /// <summary>Gets the blob container file count. Files, not non-empty files (D3).</summary>
    public long BlobFiles { get; init; }

    /// <summary>Gets the total bytes of the blob containers.</summary>
    public long BlobBytes { get; init; }

    /// <summary>Gets the data bytes: row chunks plus blob containers (FR3).</summary>
    public long DataBytes { get; init; }

    /// <summary>Gets the metadata bytes: every other file of the catalog, root files included (FR3).</summary>
    public long MetadataBytes { get; init; }

    /// <summary>Gets the catalog bytes: data plus metadata — the artifact measure (FR3, D3).</summary>
    public long CatalogBytes { get; init; }

    /// <summary>Gets how many captured tables hold no rows. Derived from rows, never from a missing chunk (D3).</summary>
    public int EmptyTableCount { get; init; }

    /// <summary>Gets the table holding the most data bytes, or <see langword="null"/> when there is none.</summary>
    public TableStat? LargestTable { get; init; }

    /// <summary>Gets the data bytes held by the largest tables, up to <see cref="ReportBudget.TopTablesForShare"/> of them (FR3).</summary>
    public long TopTablesDataBytes { get; init; }

    // ---- listings ----

    /// <summary>Gets one row per captured table, ordered by (schema, table) ordinal — every table, none omitted (FR4, FR7b).</summary>
    public IReadOnlyList<TableStat> Tables { get; init; } = [];

    /// <summary>Gets the per-schema rollup with percentages of catalog bytes (FR3).</summary>
    public IReadOnlyList<SchemaStat> Schemas { get; init; } = [];

    /// <summary>Gets the size of every metadata file, grouped as FR5 names the groups.</summary>
    public IReadOnlyList<SectionStat> MetadataFiles { get; init; } = [];

    /// <summary>
    /// Gets the schema-qualified names of the tables actually written in source order, sorted ordinal.
    /// These are the tables whose next export may come out differently (FR4).
    /// </summary>
    public IReadOnlyList<string> SourceOrderTables { get; init; } = [];
}

/// <summary>
/// What one captured table contributes: its schema facts, the bytes and rows the writer counted for it,
/// and how many foreign keys point at it or leave it.
/// </summary>
internal sealed record TableStat
{
    /// <summary>Gets the schema the table was captured under.</summary>
    public required string Schema { get; init; }

    /// <summary>Gets the table name as captured.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the number of captured columns.</summary>
    public int Columns { get; init; }

    /// <summary>Gets the rows written for this table, <c>0</c> when the table was not written at all.</summary>
    public long Rows { get; init; }

    /// <summary>Gets the row-chunk files written for this table.</summary>
    public int Chunks { get; init; }

    /// <summary>Gets the bytes of this table's chunk files.</summary>
    public long RowBytes { get; init; }

    /// <summary>Gets this table's blob container files.</summary>
    public int BlobFiles { get; init; }

    /// <summary>Gets the bytes of this table's blob containers.</summary>
    public long BlobBytes { get; init; }

    /// <summary>Gets the data bytes this table contributed — chunks plus blobs, never its metadata (FR3).</summary>
    public long DataBytes { get; init; }

    /// <summary>Gets whether the table was captured with a primary key.</summary>
    public bool HasPrimaryKey { get; init; }

    /// <summary>Gets the key summary for the table's card: primary key then unique constraints (§5.3).</summary>
    public string Keys { get; init; } = ReportText.None;

    /// <summary>Gets the foreign keys this table declares, external and self references included (§5.1).</summary>
    public int ForeignKeysOut { get; init; }

    /// <summary>Gets the foreign keys declared by captured tables that point at this table.</summary>
    public int ForeignKeysIn { get; init; }

    /// <summary>Gets the row order the snapshot declares for this table, spelled for reading.</summary>
    public string RowOrder { get; init; } = string.Empty;

    /// <summary>
    /// Gets whether this table's rows were actually written in source order (FR4). Taken from the
    /// exporter's written-table list, never from the request's default row order.
    /// </summary>
    public bool IsSourceOrder { get; init; }

    /// <summary>Gets the schema-qualified name the report uses for this table everywhere.</summary>
    public string QualifiedName => ReportText.Qualified(Schema, Name);

    /// <summary>Gets the total number of relations this table takes part in (§5.3 heading).</summary>
    public int RelationCount => ForeignKeysOut + ForeignKeysIn;
}

/// <summary>
/// One schema's share of the catalog.
/// </summary>
internal sealed record SchemaStat
{
    /// <summary>Gets the schema name as captured.</summary>
    public required string Name { get; init; }

    /// <summary>Gets how many captured tables live in this schema.</summary>
    public int Tables { get; init; }

    /// <summary>Gets the rows written for those tables.</summary>
    public long Rows { get; init; }

    /// <summary>Gets the data bytes those tables contributed.</summary>
    public long DataBytes { get; init; }
}

/// <summary>
/// One of the three metadata groups FR5 names, with every file it holds and the group's total.
/// </summary>
internal sealed record SectionStat
{
    /// <summary>Gets the group label as the report prints it.</summary>
    public required string Label { get; init; }

    /// <summary>Gets the files of this group with their recorded sizes, ordered by relative path ordinal.</summary>
    public IReadOnlyList<(string Path, long Bytes)> Files { get; init; } = [];

    /// <summary>Gets the summed bytes of this group's files.</summary>
    public long TotalBytes { get; init; }
}
