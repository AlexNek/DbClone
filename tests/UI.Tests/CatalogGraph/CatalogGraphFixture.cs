using CanonicalModel.Interchange;
using DbClone.Application.Relationship;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// Builds a <see cref="RelationshipGraphView"/> the way the reader would hand one over: identities are
/// the catalog's own references, so nothing here is keyed by the text later drawn under a node.
/// Declare a table before an edge names it.
/// </summary>
internal sealed class CatalogGraphFixture
{
    private readonly List<RelationshipTableView> _tables = [];
    private readonly List<RelationshipOutsideTableView> _outside = [];
    private readonly List<TestRelation> _edges = [];
    private GraphViewOrigin _origin = new(EGraphOriginKind.Catalog, "fixture", @"C:\catalogs\fixture", "postgresql", 3);
    private bool _hasData = true;

    public static CatalogGraphFixture Create() => new();

    public static string TableId(string schema, string name) => ObjectRef.Table(schema, name).Value;

    public CatalogGraphFixture Table(
        string schema,
        string name,
        long? rowCount = null,
        params RelationshipColumnView[] columns)
    {
        _tables.Add(new RelationshipTableView(
            TableId(schema, name),
            schema,
            name,
            rowCount,
            columns.Length,
            0,
            0,
            columns,
            null,
            [],
            [],
            []));

        return this;
    }

    /// <summary>States the primary key of the table declared last.</summary>
    public CatalogGraphFixture PrimaryKey(string name, params string[] columns)
    {
        AmendLast(table => table with { PrimaryKey = new RelationshipKeyView(name, columns) });
        return this;
    }

    /// <summary>States a unique constraint of the table declared last.</summary>
    public CatalogGraphFixture UniqueKey(string name, params string[] columns)
    {
        AmendLast(table => table with { UniqueKeys = [.. table.UniqueKeys, new RelationshipKeyView(name, columns)] });
        return this;
    }

    /// <summary>
    /// States an index of the table declared last: the captured definition text, and the key columns the
    /// catalog states — <see langword="null"/> for a folder whose producer recorded none.
    /// </summary>
    public CatalogGraphFixture Index(
        string name,
        bool unique = false,
        string? definition = null,
        string[]? columns = null)
    {
        AmendLast(table => table with
        {
            Indexes = [.. table.Indexes, new RelationshipIndexView(name, unique, definition, columns)],
            IndexCount = table.IndexCount + 1,
        });

        return this;
    }

    /// <summary>States a check constraint of the table declared last.</summary>
    public CatalogGraphFixture Check(string name, string expression)
    {
        AmendLast(table => table with
        {
            CheckConstraints = [.. table.CheckConstraints, new RelationshipCheckConstraintView(name, expression)],
        });

        return this;
    }

    private void AmendLast(Func<RelationshipTableView, RelationshipTableView> amend) =>
        _tables[^1] = amend(_tables[^1]);

    public CatalogGraphFixture Outside(string id, string? schema, string? table)
    {
        _outside.Add(new RelationshipOutsideTableView(id, schema, table));
        return this;
    }

    public CatalogGraphFixture Edge(
        string childId,
        string parentId,
        string name = "",
        string[]? columns = null,
        string[]? referencedColumns = null,
        bool mandatory = false,
        string? onDelete = null,
        string? onUpdate = null)
    {
        _edges.Add(new TestRelation(
            childId,
            parentId,
            name,
            columns ?? [],
            referencedColumns ?? [],
            mandatory,
            onDelete,
            onUpdate));

        return this;
    }

    /// <summary>Marks the catalog as one that captured no row count at all.</summary>
    public CatalogGraphFixture WithoutData()
    {
        _hasData = false;
        return this;
    }

    /// <summary>
    /// Makes the view one read from a live database: the origin states no format version and no row count
    /// is measured, so every card says so in the live words (D8, D9).
    /// </summary>
    public CatalogGraphFixture Live(string database, string location, string dialect = "postgresql")
    {
        _origin = new GraphViewOrigin(EGraphOriginKind.LiveDatabase, database, location, dialect, null);
        _hasData = false;

        return this;
    }

    public RelationshipGraphView Build()
    {
        var related = RelatedIds();
        var edges = _edges
            .Select(edge => new RelationshipEdgeView(
                edge.ChildId,
                edge.ParentId,
                edge.Name,
                edge.Columns,
                edge.ReferencedColumns,
                edge.OnDelete,
                edge.OnUpdate,
                edge.Mandatory,
                string.Equals(edge.ChildId, edge.ParentId, StringComparison.Ordinal),
                _tables.Any(table => string.Equals(table.Id, edge.ParentId, StringComparison.Ordinal))))
            .ToList();

        return new RelationshipGraphView(
            _origin,
            _hasData,
            [.. _tables],
            [.. _outside],
            Schemas(related),
            edges,
            _tables.Count(table => !related.Contains(table.Id)));
    }

    private HashSet<string> RelatedIds()
    {
        var related = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in _edges)
        {
            related.Add(edge.ChildId);
            related.Add(edge.ParentId);
        }

        return related;
    }

    private List<RelationshipSchemaView> Schemas(HashSet<string> related) =>
    [
        .. _tables
            .GroupBy(table => table.Schema, StringComparer.Ordinal)
            .Select(group => new RelationshipSchemaView(
                group.Key,
                group.Count(),
                group.Count(table => related.Contains(table.Id))))
            .OrderBy(row => row.Schema, StringComparer.Ordinal),
    ];

    private sealed record TestRelation(
        string ChildId,
        string ParentId,
        string Name,
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> ReferencedColumns,
        bool Mandatory,
        string? OnDelete,
        string? OnUpdate);
}
