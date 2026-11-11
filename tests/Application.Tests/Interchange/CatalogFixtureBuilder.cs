using System.Text.Json;
using System.Text.Json.Serialization;

using CanonicalModel.Interchange;

namespace Application.Tests.Interchange;

/// <summary>
/// Writes a small catalog folder to a temporary directory in the format the product's
/// <c>CatalogReader</c> reads: <c>manifest.json</c>, <c>catalog.json</c>,
/// <c>schema/schemas.json</c>, <c>schema/tables.json</c> and — where a row count is stated —
/// <c>data/&lt;schema&gt;/&lt;table&gt;/table.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The fixture writes the format directly rather than through the product's writer, because the
/// point of these tests is the reader and its read set: a file the fixture writes but the graph
/// read must never open is exactly how "it never touches the rows" is asserted as a fact rather
/// than as a code review.
/// </para>
/// <para>
/// Every name and count here is obviously fake, per the project's test-data rule.
/// </para>
/// </remarks>
internal sealed class CatalogFixtureBuilder : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly List<string> _schemas = [];
    private readonly List<TestTable> _tables = [];
    private readonly List<(string Schema, string Table, string Folder)> _folders = [];
    private readonly List<(string RelativePath, string Content)> _extraFiles = [];
    private int? _declaredTableCount;
    private bool _built;

    private CatalogFixtureBuilder()
    {
        _root = Path.Combine(Path.GetTempPath(), "dbclone-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>Gets the folder the catalog was written to. Only meaningful after <see cref="Build"/>.</summary>
    public string Root => _root;

    /// <summary>Creates an empty fixture.</summary>
    public static CatalogFixtureBuilder Create() => new();

    /// <summary>Adds a schema row. A schema named only by a table is added on its own.</summary>
    public CatalogFixtureBuilder Schema(string name)
    {
        _schemas.Add(name);
        return this;
    }

    /// <summary>Adds a captured table.</summary>
    public CatalogFixtureBuilder Table(TestTable table)
    {
        _tables.Add(table);
        return this;
    }

    /// <summary>
    /// States a table count in <c>catalog.json</c> that need not agree with the tables section —
    /// which is the disagreement <c>ReadGraphAsync</c> must refuse rather than draw (T19).
    /// </summary>
    public CatalogFixtureBuilder DeclaredTableCount(int count)
    {
        _declaredTableCount = count;
        return this;
    }

    /// <summary>
    /// Records a data folder in <c>catalog.json</c> without creating it on disk. The reader's own
    /// "catalog contradicts itself" refusal is what a view must surface (T19, FR10).
    /// </summary>
    public CatalogFixtureBuilder MissingDataFolder(string schema, string table)
    {
        _folders.Add((schema, table, $"{schema}/{table}"));
        return this;
    }

    /// <summary>Writes an extra file, relative to the catalog root, that the fixture does not model.</summary>
    public CatalogFixtureBuilder File(string relativePath, string content)
    {
        _extraFiles.Add((relativePath, content));
        return this;
    }

    /// <summary>Writes the catalog to disk and returns this fixture, so it can be disposed.</summary>
    public CatalogFixtureBuilder Build()
    {
        if (_built)
        {
            throw new InvalidOperationException("This fixture has already been built.");
        }

        _built = true;

        var schemaNames = new List<string>(_schemas);
        foreach (var table in _tables.Where(t => !schemaNames.Contains(t.Schema, StringComparer.Ordinal)))
        {
            schemaNames.Add(table.Schema);
        }

        var folderEntries = WriteTableData();

        WriteJson(Path.Combine(_root, "manifest.json"), new
        {
            formatVersion = 3,
            minimumReader = 3,
            producer = "fixture",
            complete = true,
            modelFormat = "interchange",
            sourceDialect = "testdialect",
            sections = new[] { "schemas", "tables" },
            counts = new
            {
                schemas = schemaNames.Count,
                tables = _tables.Count,
                columns = _tables.Sum(t => t.Columns.Count),
                rows = _tables.Sum(t => t.RowCount ?? 0),
                chunks = _tables.Count(t => t.RowCount is not null),
            },
            data = new { encoding = "csv", defaultChunkBytes = 32_768 },
            files = Array.Empty<object>(),
        });

        WriteJson(Path.Combine(_root, "catalog.json"), new
        {
            formatVersion = 3,
            sections = new[] { "schemas", "tables" },
            tableCount = _declaredTableCount ?? _tables.Count,
            tableFolders = folderEntries
                .Select(entry => new { schema = entry.Schema, table = entry.Table, folder = entry.Folder })
                .ToArray(),
        });

        WriteSection("schemas", schemaNames.Select(name => new
        {
            @ref = ObjectRef.Schema(name).Value,
            name,
        }).ToArray());

        WriteSection("tables", _tables.Select(table => new
        {
            @ref = table.Ref,
            name = table.Name,
            schemaRef = ObjectRef.Schema(table.Schema).Value,
            tableKind = table.TableKind,
            columns = table.Columns.Select((column, index) => new
            {
                @ref = ObjectRef.Column(table.Schema, table.Name, column.Name).Value,
                name = column.Name,
                tableRef = table.Ref,
                ordinal = index + 1,
                nativeType = new { nativeName = column.LogicalType },
                logicalType = column.LogicalType,
                nullable = column.Nullable,
                description = column.Comment,
            }).ToArray(),
            primaryKey = table.PrimaryKey.Count == 0 ? null : new
            {
                @ref = ObjectRef.SubObject("primarykey", table.Schema, table.Name, table.Name + "_pkey").Value,
                name = table.Name + "_pkey",
                tableRef = table.Ref,
                columns = table.PrimaryKey.ToArray(),
            },
            foreignKeys = table.ForeignKeys.Select(foreignKey => new
            {
                @ref = ObjectRef.SubObject("foreignkey", table.Schema, table.Name, foreignKey.Name).Value,
                name = foreignKey.Name,
                tableRef = table.Ref,
                columns = foreignKey.Columns.ToArray(),
                referencedTableRef = foreignKey.ParentRef,
                referencedColumns = foreignKey.ReferencedColumns.ToArray(),
                onDelete = foreignKey.OnDelete,
                onUpdate = foreignKey.OnUpdate,
            }).ToArray(),
            indexes = table.Indexes.Select(index => new
            {
                @ref = ObjectRef.SubObject("index", table.Schema, table.Name, index.Name).Value,
                name = index.Name,
                tableRef = table.Ref,
                isUnique = index.IsUnique,
                columns = index.Columns?.ToArray(),
                nativeDefinition = index.Definition is null
                    ? null
                    : new { dialect = "testdialect", text = index.Definition, complete = true },
            }).ToArray(),
            uniqueConstraints = table.UniqueConstraints.Select(key => new
            {
                @ref = ObjectRef.SubObject("uniqueconstraint", table.Schema, table.Name, key.Name).Value,
                name = key.Name,
                tableRef = table.Ref,
                columns = key.Columns.ToArray(),
            }).ToArray(),
            checkConstraints = table.CheckConstraints.Select(check => new
            {
                @ref = ObjectRef.SubObject("checkconstraint", table.Schema, table.Name, check.Name).Value,
                name = check.Name,
                tableRef = table.Ref,
                expression = new { dialect = "testdialect", text = check.Expression },
            }).ToArray(),
        }).ToArray());

        foreach (var (relativePath, content) in _extraFiles)
        {
            WriteText(relativePath, content);
        }

        return this;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A test that failed while the reader held a handle still cleans up on the next run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: the fixture's only job is to leave nothing behind that a later run would read.
        }
    }

    /// <summary>Writes each table's <c>table.json</c> and returns the folder map for <c>catalog.json</c>.</summary>
    private (string Schema, string Table, string Folder)[] WriteTableData()
    {
        var folders = new List<(string Schema, string Table, string Folder)>(_folders);

        foreach (var table in _tables)
        {
            if (table.RowCount is not { } rowCount)
            {
                continue;
            }

            var folder = $"{table.Schema}/{table.Name}";
            if (!folders.Any(entry => entry.Schema == table.Schema && entry.Table == table.Name))
            {
                folders.Add((table.Schema, table.Name, folder));
            }

            WriteJson(
                Path.Combine(_root, "data", folder.Replace('/', Path.DirectorySeparatorChar), "table.json"),
                new
                {
                    id = table.Ref,
                    schema = table.Schema,
                    name = table.Name,
                    columns = table.Columns
                        .Select(column => new { name = column.Name, logicalType = column.LogicalType })
                        .ToArray(),
                    rowCount,
                    rowOrder = "primaryKey",
                    chunkCount = 1,
                });
        }

        return [.. folders];
    }

    private void WriteSection(string sectionName, object[] objects) =>
        WriteJson(Path.Combine(_root, "schema", sectionName + ".json"), new { objects });

    private void WriteJson<T>(string path, T value) =>
        WriteText(Path.Combine(_root, path), JsonSerializer.Serialize(value, JsonOptions));

    private void WriteText(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }
}

/// <summary>One captured table in a fixture catalog.</summary>
internal sealed class TestTable
{
    /// <summary>Initializes a table named by its schema and table names.</summary>
    public TestTable(string schema, string name)
    {
        Schema = schema;
        Name = name;
    }

    /// <summary>Gets the schema name.</summary>
    public string Schema { get; }

    /// <summary>Gets the table name.</summary>
    public string Name { get; }

    /// <summary>Gets the table kind, as the catalog spells it.</summary>
    public string TableKind { get; init; } = "table";

    /// <summary>Gets the columns, in the order the table declares them.</summary>
    public IReadOnlyList<TestColumn> Columns { get; init; } = [];

    /// <summary>Gets the primary-key column names.</summary>
    public IReadOnlyList<string> PrimaryKey { get; init; } = [];

    /// <summary>Gets the foreign keys the table declares.</summary>
    public IReadOnlyList<TestForeignKey> ForeignKeys { get; init; } = [];

    /// <summary>
    /// Gets the indexes. An index states a name, its uniqueness, the key columns it covers and — when
    /// the producer captured it — the definition text the engine reported.
    /// </summary>
    public IReadOnlyList<TestIndex> Indexes { get; init; } = [];

    /// <summary>Gets the unique constraints the table declares.</summary>
    public IReadOnlyList<TestKey> UniqueConstraints { get; init; } = [];

    /// <summary>Gets the check constraints the table declares.</summary>
    public IReadOnlyList<TestCheck> CheckConstraints { get; init; } = [];

    /// <summary>
    /// Gets the row count the table's <c>table.json</c> states, or <see langword="null"/> when the
    /// run captured no data for it — the two facts the view must never confuse.
    /// </summary>
    public long? RowCount { get; init; }

    /// <summary>Gets the table's reference value, which is the node key the view must use.</summary>
    public string Ref => ObjectRef.Table(Schema, Name).Value;
}

/// <summary>One column in a fixture table.</summary>
/// <param name="Name">The column name.</param>
/// <param name="Nullable">Whether the column admits a null.</param>
/// <param name="LogicalType">The logical type, as the catalog spells it.</param>
/// <param name="Comment">The captured comment, when the table has one.</param>
internal sealed record TestColumn(
    string Name,
    bool Nullable = false,
    string LogicalType = "string",
    string? Comment = null);

/// <summary>One foreign key in a fixture table.</summary>
/// <param name="Name">The constraint's captured name; empty when the constraint is unnamed.</param>
/// <param name="ParentRef">
/// The referenced table's <see cref="ObjectRef.Value"/> verbatim. An empty value is the product's
/// own "states nothing" case; a value with no dot is its "name but no schema" case.
/// </param>
/// <param name="Columns">The referencing column names, in the constraint's own order.</param>
/// <param name="ReferencedColumns">The referenced column names, in the constraint's own order.</param>
/// <param name="OnDelete">The <c>ON DELETE</c> text, or null when the catalog leaves it out.</param>
/// <param name="OnUpdate">The <c>ON UPDATE</c> text, or null when the catalog leaves it out.</param>
internal sealed record TestForeignKey(
    string Name,
    string ParentRef,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> ReferencedColumns,
    string? OnDelete = null,
    string? OnUpdate = null);

/// <summary>One index in a fixture table.</summary>
/// <param name="Name">The index's captured name.</param>
/// <param name="IsUnique">Whether the index is unique.</param>
/// <param name="Definition">The definition text the producer captured, or null when it captured none.</param>
/// <param name="Columns">
/// The key columns the producer stated, or null for a folder written before the list was recorded — the
/// fixture keeps both shapes writable because the panel treats them differently.
/// </param>
internal sealed record TestIndex(
    string Name,
    bool IsUnique = false,
    string? Definition = null,
    IReadOnlyList<string>? Columns = null);

/// <summary>One key in a fixture table: a unique constraint, or the primary key's sibling.</summary>
/// <param name="Name">The key's captured name.</param>
/// <param name="Columns">The key's column names, in the key's own order.</param>
internal sealed record TestKey(
    string Name,
    IReadOnlyList<string> Columns);

/// <summary>One check constraint in a fixture table.</summary>
/// <param name="Name">The constraint's captured name.</param>
/// <param name="Expression">The expression text as captured.</param>
internal sealed record TestCheck(
    string Name,
    string Expression);
