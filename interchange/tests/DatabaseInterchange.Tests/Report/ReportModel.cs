using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DatabaseInterchange.Report;

namespace DatabaseInterchange.Tests.Report;

/// <summary>
/// Object factories shared by the report tests.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is an obviously-fake fixture: the model is built in memory, so no test opens a
/// database and none of these helpers touches a filesystem. The names follow the security rule of FR9 —
/// <c>testdb</c>, <c>test.example.com</c>, <c>test_user</c> — never a real connection's.
/// </para>
/// <para>
/// A snapshot built without the optional sections is a schema-only capture with no security and no
/// lineage, which is what every builder test that does not care about them should read as.
/// </para>
/// </remarks>
internal static class ReportModel
{
    internal const string Database = "testdb";

    internal const string Dialect = "postgresql";

    internal const string Producer = "DbClone-test";

    /// <summary>The instant every statistics fixture shares, so a golden text has a stable stamp.</summary>
    internal static readonly DateTimeOffset ExportedAt =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    internal static SchemaDefinition Schema(string name) =>
        new() { Ref = ObjectRef.Schema(name), Name = name };

    internal static ColumnDefinition Column(
        string schema,
        string table,
        string name,
        bool nullable = true,
        string nativeType = "integer") =>
        new()
        {
            Ref = ObjectRef.Column(schema, table, name),
            Name = name,
            TableRef = ObjectRef.Table(schema, table),
            Ordinal = 1,
            Nullable = nullable,
            NativeType = new NativeType(nativeType),
        };

    internal static PrimaryKeyDefinition PrimaryKey(string schema, string table, params string[] columns) =>
        new()
        {
            Ref = ObjectRef.SubObject("constraint", schema, table, $"{table}_pkey"),
            Name = $"{table}_pkey",
            TableRef = ObjectRef.Table(schema, table),
            Columns = [.. columns],
        };

    internal static UniqueConstraintDefinition Unique(
        string schema, string table, string name, params string[] columns) =>
        new()
        {
            Ref = ObjectRef.SubObject("constraint", schema, table, name),
            Name = name,
            TableRef = ObjectRef.Table(schema, table),
            Columns = [.. columns],
        };

    /// <summary>
    /// One foreign key. The parent is addressed by schema, table and column list exactly as a captured
    /// constraint states it, so an unparented target has to be built by hand in the test that needs one.
    /// </summary>
    internal static ForeignKeyDefinition Reference(
        string childSchema,
        string childTable,
        string name,
        string parentSchema,
        string parentTable,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> referencedColumns,
        string? onDelete = null,
        string? onUpdate = null) =>
        new()
        {
            Ref = ObjectRef.SubObject("constraint", childSchema, childTable, name),
            Name = name,
            TableRef = ObjectRef.Table(childSchema, childTable),
            ReferencedTableRef = ObjectRef.Table(parentSchema, parentTable),
            Columns = [.. columns],
            ReferencedColumns = [.. referencedColumns],
            OnDelete = onDelete,
            OnUpdate = onUpdate,
        };

    /// <summary>A constraint whose target the snapshot does not name: the unusable-reference shape.</summary>
    internal static ForeignKeyDefinition UnresolvedReference(
        string childSchema, string childTable, string name, ObjectRef target, string column) =>
        new()
        {
            Ref = ObjectRef.SubObject("constraint", childSchema, childTable, name),
            Name = name,
            TableRef = ObjectRef.Table(childSchema, childTable),
            ReferencedTableRef = target,
            Columns = [column],
            ReferencedColumns = [],
        };

    internal static TableDefinition Table(string schema, string name, params ColumnDefinition[] columns) =>
        new()
        {
            Ref = ObjectRef.Table(schema, name),
            Name = name,
            SchemaRef = ObjectRef.Schema(schema),
            Columns = [.. columns],
        };

    /// <summary>A table with a primary key over its first column — the shape most fixtures need.</summary>
    internal static TableDefinition KeyedTable(string schema, string name, string keyColumn = "id")
    {
        var id = Column(schema, name, keyColumn, nullable: false);

        return Table(schema, name, id, Column(schema, name, "name")) with
        {
            PrimaryKey = PrimaryKey(schema, name, keyColumn),
        };
    }

    internal static InterchangeSnapshot Snapshot(params TableDefinition[] tables) =>
        SnapshotFrom([.. tables]);

    /// <summary>
    /// A snapshot whose schema section carries every schema the tables point at, which is how the writer
    /// resolved them and therefore how the report must join them.
    /// </summary>
    internal static InterchangeSnapshot SnapshotFrom(IReadOnlyList<TableDefinition> tables) =>
        new()
        {
            ProducedBy = Producer,
            Database = new DatabaseInfo { Name = Database },
            Schemas =
            [
                .. tables
                    .Select(t => t.SchemaRef)
                    .Distinct()
                    .OrderBy(r => r.Value, StringComparer.Ordinal)
                    .Select(r => new SchemaDefinition { Ref = r, Name = r.ObjectName() }),
            ],
            Tables = tables,
        };

    /// <summary>
    /// One hub table referenced by <paramref name="dependents"/> spokes, each declaring
    /// <paramref name="constraintsPerDependent"/> constraints: every relation in a single component, which
    /// is the shape the completeness counts are asserted against.
    /// </summary>
    internal static InterchangeSnapshot Star(int dependents, int constraintsPerDependent = 1)
    {
        var tables = new List<TableDefinition> { KeyedTable("public", "hub") };

        for (var spoke = 0; spoke < dependents; spoke++)
        {
            var name = $"dep_{spoke:0000}";
            var keys = new List<string>();
            var references = new List<ForeignKeyDefinition>();

            for (var ordinal = 0; ordinal < constraintsPerDependent; ordinal++)
            {
                var column = ordinal == 0 ? "hub_id" : $"hub_id_{ordinal}";
                keys.Add(column);
                references.Add(Reference(
                    "public", name, $"fk_{name}_{ordinal}", "public", "hub",
                    [column], ["id"]));
            }

            tables.Add(Table("public", name, [
                .. keys.Select(k => Column("public", name, k, nullable: false)),
                Column("public", name, "note"),
            ]) with
            {
                PrimaryKey = PrimaryKey("public", name, [.. keys]),
                ForeignKeys = [.. references],
            });
        }

        return SnapshotFrom(tables);
    }

    /// <summary><paramref name="tables"/> unrelated keyed tables in one schema, with no relations.</summary>
    internal static InterchangeSnapshot WideSchema(int tables) =>
        SnapshotFrom([
            .. Enumerable.Range(0, tables).Select(i => KeyedTable("public", $"table_{i:0000}")),
        ]);

    internal static TableWriteStats Written(
        string schema,
        string table,
        long rows = 0,
        int chunks = 0,
        long rowBytes = 0,
        int blobFiles = 0,
        long blobBytes = 0) =>
        new(schema, table, rows, chunks, rowBytes, blobFiles, blobBytes);

    /// <summary>
    /// The writer's per-table facts for a whole snapshot, one row each, so a renderer test that is not
    /// about the data figures still gets a catalog that reads as having been written.
    /// </summary>
    internal static IReadOnlyList<TableWriteStats> WrittenEveryTable(InterchangeSnapshot snapshot) =>
        [
            .. snapshot.Tables.Select(t => Written(
                t.SchemaRef.ObjectName(), t.Name, rows: 10, chunks: 1, rowBytes: 120, blobBytes: 0)),
        ];

    /// <summary>
    /// Runs the real builder over a snapshot, so a test of the text never restates a number by hand and
    /// cannot drift from the model it is checking.
    /// </summary>
    internal static CatalogStatistics Statistics(
        InterchangeSnapshot snapshot,
        IReadOnlyList<TableWriteStats>? written = null,
        IReadOnlyList<(string Path, long Bytes)>? metadataFiles = null,
        long rootJsonBytes = 0,
        IReadOnlyList<string>? sections = null,
        DateTimeOffset? exportedAt = null,
        int excludedTables = 0,
        IReadOnlyList<TableRef>? sourceOrderTables = null) =>
        new CatalogStatisticsBuilder().Build(
            snapshot,
            written ?? WrittenEveryTable(snapshot),
            metadataFiles ?? [],
            rootJsonBytes,
            sections ?? ["schemas", "tables", "columns", "views"],
            exportedAt ?? ExportedAt,
            Dialect,
            "consistent snapshot",
            excludedTables,
            sourceOrderTables ?? []);

    /// <summary>Analyses the relation facts of a snapshot exactly the way the exporter does.</summary>
    internal static RelationMap MapOf(InterchangeSnapshot snapshot) =>
        new RelationGraphBuilder().Build(snapshot);

    /// <summary>Renders the complete document for a snapshot through the two real renderers.</summary>
    internal static string Document(InterchangeSnapshot snapshot, CatalogStatistics? statistics = null) =>
        new MarkdownCatalogReportRenderer().Render(
            statistics ?? Statistics(snapshot),
            MapOf(snapshot));
}
