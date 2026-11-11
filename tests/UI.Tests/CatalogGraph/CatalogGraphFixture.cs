using CanonicalModel.Interchange;
using DbClone.Application.Interchange;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// Builds a <see cref="CatalogGraphView"/> the way the reader would hand one over: identities are
/// the catalog's own references, so nothing here is keyed by the text later drawn under a node.
/// Declare a table before an edge names it.
/// </summary>
internal sealed class CatalogGraphFixture
{
    private readonly List<CatalogTableView> _tables = [];
    private readonly List<CatalogOutsideTableView> _outside = [];
    private readonly List<TestRelation> _edges = [];
    private bool _hasData = true;

    public static CatalogGraphFixture Create() => new();

    public static string TableId(string schema, string name) => ObjectRef.Table(schema, name).Value;

    public CatalogGraphFixture Table(
        string schema,
        string name,
        long? rowCount = null,
        params CatalogColumnView[] columns)
    {
        _tables.Add(new CatalogTableView(
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
        AmendLast(table => table with { PrimaryKey = new CatalogKeyView(name, columns) });
        return this;
    }

    /// <summary>States a unique constraint of the table declared last.</summary>
    public CatalogGraphFixture UniqueKey(string name, params string[] columns)
    {
        AmendLast(table => table with { UniqueKeys = [.. table.UniqueKeys, new CatalogKeyView(name, columns)] });
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
            Indexes = [.. table.Indexes, new CatalogIndexView(name, unique, definition, columns)],
            IndexCount = table.IndexCount + 1,
        });

        return this;
    }

    /// <summary>States a check constraint of the table declared last.</summary>
    public CatalogGraphFixture Check(string name, string expression)
    {
        AmendLast(table => table with
        {
            CheckConstraints = [.. table.CheckConstraints, new CatalogCheckConstraintView(name, expression)],
        });

        return this;
    }

    private void AmendLast(Func<CatalogTableView, CatalogTableView> amend) =>
        _tables[^1] = amend(_tables[^1]);

    public CatalogGraphFixture Outside(string id, string? schema, string? table)
    {
        _outside.Add(new CatalogOutsideTableView(id, schema, table));
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

    public CatalogGraphView Build()
    {
        var related = RelatedIds();
        var edges = _edges
            .Select(edge => new CatalogRelationEdgeView(
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

        return new CatalogGraphView(
            @"C:\catalogs\fixture",
            3,
            "postgresql",
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

    private List<CatalogSchemaView> Schemas(HashSet<string> related) =>
    [
        .. _tables
            .GroupBy(table => table.Schema, StringComparer.Ordinal)
            .Select(group => new CatalogSchemaView(
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
