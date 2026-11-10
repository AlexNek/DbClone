using CanonicalModel.Interchange;
using DatabaseInterchange.Format;
using DbAdapter.Definitions;

namespace DatabaseInterchange.Report;

/// <summary>
/// Joins the captured snapshot with the numbers the writer kept while it wrote (D3) into the
/// render-ready <see cref="CatalogStatistics"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pure: no file is opened, no database is queried, no folder is enumerated. Every byte figure arrives
/// as one of the writer's own measures — the per-table statistics, the manifest's file projection and
/// the length of the two root files — so the report can never state a total that the catalog's files do
/// not add up to.
/// </para>
/// <para>
/// A captured table with no <see cref="TableWriteStats"/> entry was excluded from the data path of this
/// store. It still gets a row, with zero data, because FR10 prints every captured table: a listing that
/// stopped early would leave tables the catalog holds named in no line at all.
/// </para>
/// </remarks>
internal sealed class CatalogStatisticsBuilder
{
    /// <summary>
    /// Builds the statistics model for one finished catalog.
    /// </summary>
    /// <param name="snapshot">The captured model the catalog was written from.</param>
    /// <param name="tableStats">The writer's per-table numbers, one per table it wrote.</param>
    /// <param name="metadataFileSizes">The writer's manifest projection: path and bytes per metadata file.</param>
    /// <param name="rootJsonBytes">The combined length of <c>manifest.json</c> and <c>export.json</c>.</param>
    /// <param name="sectionNames">The section names the writer finished with.</param>
    /// <param name="exportedAt">The instant <c>export.json</c> carries, or <see langword="null"/>.</param>
    /// <param name="sourceDialect">The dialect the writer recorded.</param>
    /// <param name="consistency">The consistency mode actually used, already spelled for reading.</param>
    /// <param name="excludedTableCount">How many tables the selection excluded (FR17).</param>
    /// <param name="sourceOrderTables">
    /// The tables actually written in source order. This is the only source for the source-order mark:
    /// re-deriving it from the request's default row order would name tables the catalog never captured.
    /// </param>
    public CatalogStatistics Build(
        InterchangeSnapshot snapshot,
        IReadOnlyList<TableWriteStats> tableStats,
        IReadOnlyList<(string Path, long Bytes)> metadataFileSizes,
        long rootJsonBytes,
        IReadOnlyList<string> sectionNames,
        DateTimeOffset? exportedAt,
        string sourceDialect,
        string consistency,
        int excludedTableCount,
        IReadOnlyList<TableRef> sourceOrderTables)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(tableStats);
        ArgumentNullException.ThrowIfNull(metadataFileSizes);
        ArgumentNullException.ThrowIfNull(sectionNames);
        ArgumentNullException.ThrowIfNull(sourceOrderTables);

        var schemaNames = ForeignKeyEdge.SchemaNamesOf(snapshot);
        var written = IndexWriteStats(tableStats);
        var sourceOrder = ToKeySet(sourceOrderTables);
        var fanIn = CountIncomingReferences(snapshot);
        var hasData = tableStats.Count > 0;

        var rows = new List<TableStat>(snapshot.Tables.Count);
        foreach (var table in snapshot.Tables)
        {
            rows.Add(BuildTableStat(table, schemaNames, written, fanIn, sourceOrder));
        }

        var tables = rows
            .OrderBy(r => r.Schema, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        var dataBytes = tables.Sum(t => t.DataBytes);
        var metadataBytes = metadataFileSizes.Sum(f => f.Bytes) + rootJsonBytes;

        return new CatalogStatistics
        {
            DatabaseName = snapshot.Database.Name,
            Producer = snapshot.ProducedBy,
            SourceDialect = sourceDialect,
            FormatVersion = snapshot.FormatVersion,
            SectionNames = [.. sectionNames],
            ExportedAt = exportedAt,
            Consistency = consistency,
            ExcludedTableCount = excludedTableCount,

            SchemaCount = snapshot.Schemas.Count,
            TableCount = snapshot.Tables.Count,
            ColumnCount = snapshot.Tables.Sum(t => t.Columns.Count),
            ViewCount = snapshot.Views.Count(v => !v.IsMaterialized),
            MaterializedViewCount = snapshot.Views.Count(v => v.IsMaterialized),
            RoutineCount = snapshot.Routines.Count,
            SequenceCount = snapshot.Sequences.Count,
            ExtensionCount = snapshot.Extensions.Count,
            ForeignKeyCount = snapshot.Tables.Sum(t => t.ForeignKeys.Count),
            ViewReferenceCount = snapshot.Relationships.Count(r =>
                string.Equals(r.Type, RelationGraphBuilder.ViewReferenceType, StringComparison.Ordinal)),
            RoleCount = snapshot.Security?.Roles.Count ?? 0,
            GrantCount = snapshot.Security?.Grants.Count ?? 0,
            PolicyCount = snapshot.Security?.Policies.Count ?? 0,

            HasData = hasData,
            Rows = tables.Sum(t => t.Rows),
            ChunkFiles = tables.Sum(t => t.Chunks),
            RowBytes = tables.Sum(t => t.RowBytes),
            BlobFiles = tables.Sum(t => t.BlobFiles),
            BlobBytes = tables.Sum(t => t.BlobBytes),
            DataBytes = dataBytes,
            MetadataBytes = metadataBytes,
            CatalogBytes = dataBytes + metadataBytes,
            EmptyTableCount = hasData ? tables.Count(t => t.Rows == 0) : 0,
            LargestTable = FindLargest(tables, dataBytes),
            TopTablesDataBytes = SumTopTables(tables),

            Tables = tables,
            Schemas = BuildSchemaRollup(tables),
            MetadataFiles = BuildMetadataGroups(metadataFileSizes, rootJsonBytes),
            SourceOrderTables = [.. sourceOrderTables
                .Select(t => ReportText.Qualified(t.Schema, t.Name))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// Indexes the writer's per-table facts by the same <c>(schema, table)</c> key the report lists tables
    /// under. A table the data path skipped has no entry, which is how an excluded table reaches the
    /// listing with zero data instead of disappearing from it.
    /// </summary>
    private static Dictionary<(string Schema, string Table), TableWriteStats> IndexWriteStats(
        IReadOnlyList<TableWriteStats> tableStats) =>
        tableStats
            .GroupBy(s => (s.Schema, s.Table))
            .ToDictionary(g => g.Key, g => g.First());

    private static HashSet<(string Schema, string Table)> ToKeySet(IReadOnlyList<TableRef> tables) =>
        [.. tables.Select(t => (t.Schema, t.Name))];

    /// <summary>
    /// Counts, per referenced table, how many constraints captured tables declare against it. One
    /// constraint is one relation, so a duplicate pair adds twice (§5.1) and a self reference adds one
    /// to its own table's inbound count.
    /// </summary>
    private static Dictionary<(string Schema, string Table), int> CountIncomingReferences(
        InterchangeSnapshot snapshot)
    {
        var counts = new Dictionary<(string Schema, string Table), int>();
        foreach (var table in snapshot.Tables)
        {
            foreach (var foreignKey in table.ForeignKeys)
            {
                if (!ForeignKeyEdge.TryResolveTable(foreignKey.ReferencedTableRef, out var schema, out var name))
                {
                    continue;
                }

                counts[(schema, name)] = counts.TryGetValue((schema, name), out var existing) ? existing + 1 : 1;
            }
        }

        return counts;
    }

    private static TableStat BuildTableStat(
        TableDefinition table,
        IReadOnlyDictionary<ObjectRef, string> schemaNames,
        Dictionary<(string Schema, string Table), TableWriteStats> written,
        Dictionary<(string Schema, string Table), int> fanIn,
        HashSet<(string Schema, string Table)> sourceOrder)
    {
        // The schema name is resolved the way the writer resolved it — through the captured schema
        // section — so this join key is the same one catalog.json and the data folders carry.
        var schema = ForeignKeyEdge.SchemaOf(table, schemaNames);
        var key = (schema, table.Name);
        var stats = written.TryGetValue(key, out var entry) ? entry : null;

        return new TableStat
        {
            Schema = schema,
            Name = table.Name,
            Columns = table.Columns.Count,
            Rows = stats?.Rows ?? 0,
            Chunks = stats?.Chunks ?? 0,
            RowBytes = stats?.RowBytes ?? 0,
            BlobFiles = stats?.BlobFiles ?? 0,
            BlobBytes = stats?.BlobBytes ?? 0,
            DataBytes = stats?.DataBytes ?? 0,
            HasPrimaryKey = table.PrimaryKey is not null,
            Keys = RenderKeys(table),
            ForeignKeysOut = table.ForeignKeys.Count,
            ForeignKeysIn = fanIn.TryGetValue(key, out var inbound) ? inbound : 0,
            RowOrder = ReportText.Words(table.RowOrder.ToString()),
            IsSourceOrder = sourceOrder.Contains(key),
        };
    }

    /// <summary>
    /// Renders the card's <c>Keys</c> line (§5.3): the primary key first, then every unique constraint.
    /// A single-column key carries its native type; a composite key is the parenthesised column list.
    /// Every constraint the table holds is printed, none summarised.
    /// </summary>
    private static string RenderKeys(TableDefinition table)
    {
        var parts = new List<string>();

        if (table.PrimaryKey is { } primaryKey)
        {
            parts.Add(RenderKey(table, primaryKey.Columns) + " PK");
        }

        foreach (var unique in table.UniqueConstraints)
        {
            parts.Add(RenderKey(table, unique.Columns) + " UK");
        }

        return parts.Count == 0 ? ReportText.None : string.Join(ReportBudget.FactSeparator, parts);
    }

    private static string RenderKey(TableDefinition table, IReadOnlyList<string> columns)
    {
        if (columns.Count != 1)
        {
            return $"({string.Join(", ", columns.Select(ReportText.Cell))})";
        }

        var name = columns[0];
        var type = table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
            ?.NativeType.Name;

        return string.IsNullOrEmpty(type)
            ? ReportText.Code(name)
            : $"{ReportText.Code(name)} ({ReportText.Code(type)})";
    }

    /// <summary>
    /// Picks the table holding the most data bytes. Ties break on <c>(schema, table)</c> ordinal (D9.1);
    /// an all-empty catalog has no largest table and the cell reads <c>none</c>.
    /// </summary>
    private static TableStat? FindLargest(List<TableStat> tables, long dataBytes)
    {
        if (dataBytes <= 0)
        {
            return null;
        }

        return tables
            .OrderByDescending(t => t.DataBytes)
            .ThenBy(t => t.Schema, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Sums the data bytes of the largest <see cref="ReportBudget.TopTablesForShare"/> tables (FR3).</summary>
    private static long SumTopTables(List<TableStat> tables) =>
        tables
            .OrderByDescending(t => t.DataBytes)
            .ThenBy(t => t.Schema, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Take(ReportBudget.TopTablesForShare)
            .Sum(t => t.DataBytes);

    private static IReadOnlyList<SchemaStat> BuildSchemaRollup(List<TableStat> tables) =>
        tables
            .GroupBy(t => t.Schema)
            .Select(g => new SchemaStat
            {
                Name = g.Key,
                Tables = g.Count(),
                Rows = g.Sum(t => t.Rows),
                DataBytes = g.Sum(t => t.DataBytes),
            })
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Groups every metadata file the writer produced into the three groups FR5 names, each file with
    /// its own row. The two root files are reported as one combined row, because the only figure the
    /// writer can keep for them is their summed length (D3).
    /// </summary>
    private static IReadOnlyList<SectionStat> BuildMetadataGroups(
        IReadOnlyList<(string Path, long Bytes)> metadataFileSizes,
        long rootJsonBytes)
    {
        const string schemaPrefix = InterchangeFormat.SchemaDirectory + "/";
        const string nativePrefix = InterchangeFormat.NativeDirectory + "/";

        var sections = metadataFileSizes
            .Where(f => f.Path.StartsWith(schemaPrefix, StringComparison.Ordinal))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        var native = metadataFileSizes
            .Where(f => f.Path.StartsWith(nativePrefix, StringComparison.Ordinal))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        var perTable = metadataFileSizes
            .Where(f => !f.Path.StartsWith(schemaPrefix, StringComparison.Ordinal)
                        && !f.Path.StartsWith(nativePrefix, StringComparison.Ordinal))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        if (rootJsonBytes > 0)
        {
            perTable.Add((ReportText.RootMetadataFiles, rootJsonBytes));
        }

        return
        [
            new SectionStat
            {
                Label = "schema sections",
                Files = sections,
                TotalBytes = sections.Sum(f => f.Bytes),
            },
            new SectionStat
            {
                Label = "engine native",
                Files = native,
                TotalBytes = native.Sum(f => f.Bytes),
            },
            new SectionStat
            {
                Label = "catalog and per-table metadata",
                Files = perTable,
                TotalBytes = perTable.Sum(f => f.Bytes),
            },
        ];
    }
}
