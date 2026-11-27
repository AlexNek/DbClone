using System.Globalization;

using CanonicalModel.Models;

namespace DbClone.Application.Relationship;

/// <summary>
/// The whole conversion from a database model to a relationship view: tables, primary and unique keys,
/// indexes, check constraints, and one edge per foreign key, with node identities assigned rather than
/// concatenated.
/// </summary>
/// <remarks>
/// <para>
/// The catalog reader and this mapper produce the same shape, because the analyzer, the layout and the
/// document must not be able to tell a live read from a stored one. What differs is only what the origin
/// can state: a live read counts no rows (D9) and declares no format version.
/// </para>
/// <para>
/// Nothing is parsed out of SQL: a foreign key states engine-native schema and table strings, a primary
/// key is the index the model marks as primary, and a check carries its expression. A reference that
/// states nothing becomes its own node instead of an invented name.
/// </para>
/// </remarks>
public static class DatabaseModelGraphMapper
{
    /// <summary>Converts a read model into the view the document pipeline consumes.</summary>
    /// <param name="model">The database model the adapter returned.</param>
    /// <param name="origin">The origin the read came from; it is carried, never derived here.</param>
    /// <returns>The origin's relationship view.</returns>
    public static RelationshipGraphView Map(DatabaseModel model, GraphViewOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(origin);

        var captured = CapturedTables(model);

        // Node identity is assigned, never built by joining names: a table called `a.b` in schema `c`
        // must not collide with schema `a` and table `b.c`.
        var idByKey = captured.ToDictionary(
            entry => (entry.Table.SchemaName, entry.Table.Name),
            entry => entry.Id);
        var outsideIds = OutsideIds(captured, idByKey);

        var edges = new List<RelationshipEdgeView>();
        var outsideViews = OutsideViews(outsideIds);
        var referencingColumns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var referencedColumns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (table, childId) in captured)
        {
            var columnsByName = new Dictionary<string, ColumnDefinition>(table.Columns.Count, StringComparer.Ordinal);
            foreach (var column in table.Columns)
            {
                columnsByName[column.Name] = column;
            }

            foreach (var foreignKey in table.ForeignKeys)
            {
                var (parentId, parentIsCaptured) = ResolveParent(
                    foreignKey,
                    childId,
                    idByKey,
                    outsideIds,
                    outsideViews);

                edges.Add(new RelationshipEdgeView(
                    ChildId: childId,
                    ParentId: parentId,
                    ConstraintName: foreignKey.Name,
                    Columns: [.. foreignKey.Columns],
                    ReferencedColumns: [.. foreignKey.ReferencedColumns],
                    OnDelete: NormalizeAction(foreignKey.DeleteRule),
                    OnUpdate: NormalizeAction(foreignKey.UpdateRule),
                    IsMandatory: IsMandatory(columnsByName, foreignKey.Columns),
                    IsSelfReference: parentIsCaptured && string.Equals(parentId, childId, StringComparison.Ordinal),
                    ParentIsCaptured: parentIsCaptured));

                var referencing = GetOrAdd(referencingColumns, childId);
                foreach (var column in foreignKey.Columns)
                {
                    referencing.Add(column);
                }

                if (parentIsCaptured)
                {
                    var referenced = GetOrAdd(referencedColumns, parentId);
                    foreach (var column in foreignKey.ReferencedColumns)
                    {
                        referenced.Add(column);
                    }
                }
            }
        }

        var tableViews = captured
            .Select(entry => TableView(entry.Table, entry.Id, referencingColumns, referencedColumns))
            .ToList();

        var relatedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            relatedIds.Add(edge.ChildId);
            if (edge.ParentIsCaptured)
            {
                relatedIds.Add(edge.ParentId);
            }
        }

        var schemaViews = captured
            .GroupBy(entry => entry.Table.SchemaName, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new RelationshipSchemaView(
                Schema: group.Key,
                TableCount: group.Count(),
                RelatedTableCount: group.Count(entry => relatedIds.Contains(entry.Id))))
            .ToList();

        return new RelationshipGraphView(
            Origin: origin,
            // A live read states no count for any table, and says so in the words its origin supplies (D9).
            HasData: false,
            Tables: tableViews,
            OutsideTables: [.. outsideViews.OrderBy(view => view.Id, StringComparer.Ordinal)],
            Schemas: schemaViews,
            Edges: [.. edges
                .OrderBy(edge => edge.ChildId, StringComparer.Ordinal)
                .ThenBy(edge => edge.ConstraintName, StringComparer.Ordinal)
                .ThenBy(edge => edge.ParentId, StringComparer.Ordinal)],
            UnrelatedTableCount: captured.Count(entry => !relatedIds.Contains(entry.Id)));
    }

    private static List<(TableDefinition Table, string Id)> CapturedTables(DatabaseModel model)
    {
        var ordered = model.Tables
            .OrderBy(table => table.SchemaName, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal);

        var captured = new List<(TableDefinition Table, string Id)>(model.Tables.Count);
        foreach (var table in ordered)
        {
            captured.Add((table, "t" + (captured.Count + 1).ToString("D4", CultureInfo.InvariantCulture)));
        }

        return captured;
    }

    /// <summary>
    /// Assigns <c>o0001…</c> to every target the origin does not hold, in the target's own sorted order,
    /// so the same database numbers the same outside nodes twice.
    /// </summary>
    private static Dictionary<(string Schema, string Table), string> OutsideIds(
        List<(TableDefinition Table, string Id)> captured,
        Dictionary<(string Schema, string Name), string> idByKey)
    {
        var keys = new SortedSet<(string Schema, string Table)>(OutsideKeyOrder.Instance);
        foreach (var (table, _) in captured)
        {
            foreach (var foreignKey in table.ForeignKeys)
            {
                if (!StatesTarget(foreignKey) || idByKey.ContainsKey((foreignKey.ReferencedSchema, foreignKey.ReferencedTable)))
                {
                    continue;
                }

                keys.Add((foreignKey.ReferencedSchema, foreignKey.ReferencedTable));
            }
        }

        var ids = new Dictionary<(string Schema, string Table), string>(keys.Count);
        foreach (var key in keys)
        {
            ids[key] = "o" + (ids.Count + 1).ToString("D4", CultureInfo.InvariantCulture);
        }

        return ids;
    }

    private static List<RelationshipOutsideTableView> OutsideViews(
        Dictionary<(string Schema, string Table), string> outsideIds)
    {
        var views = new List<RelationshipOutsideTableView>(outsideIds.Count);
        foreach (var (key, id) in outsideIds)
        {
            views.Add(new RelationshipOutsideTableView(
                Id: id,
                Schema: string.IsNullOrEmpty(key.Schema) ? null : key.Schema,
                Table: key.Table));
        }

        return views;
    }

    private static (string ParentId, bool ParentIsCaptured) ResolveParent(
        ForeignKeyDefinition foreignKey,
        string childId,
        Dictionary<(string Schema, string Name), string> idByKey,
        Dictionary<(string Schema, string Table), string> outsideIds,
        List<RelationshipOutsideTableView> outsideViews)
    {
        if (!StatesTarget(foreignKey))
        {
            // The reference states nothing. The key only keeps two nameless targets apart; the label is
            // the renderer's business, never a name invented here.
            var unresolved = $"unresolved:{childId}|{foreignKey.Name}";
            outsideViews.Add(new RelationshipOutsideTableView(unresolved, null, null));
            return (unresolved, false);
        }

        var key = (foreignKey.ReferencedSchema, foreignKey.ReferencedTable);
        return idByKey.TryGetValue(key, out var capturedParentId)
            ? (capturedParentId, true)
            : (outsideIds[key], false);
    }

    private static RelationshipTableView TableView(
        TableDefinition table,
        string id,
        Dictionary<string, HashSet<string>> referencingColumns,
        Dictionary<string, HashSet<string>> referencedColumns)
    {
        referencingColumns.TryGetValue(id, out var referencing);
        referencedColumns.TryGetValue(id, out var referenced);

        var primaryKey = table.Indexes.FirstOrDefault(index => index.IsPrimary);

        var columns = table.Columns
            .OrderBy(column => column.OrdinalPosition)
            .Select(column => new RelationshipColumnView(
                Name: column.Name,
                LogicalType: column.DataType,
                IsNullable: column.IsNullable,
                IsPartOfPrimaryKey: primaryKey?.Columns.Contains(column.Name, StringComparer.Ordinal) == true,
                IsReferencingForeignKey: referencing?.Contains(column.Name) == true,
                IsReferencedByForeignKey: referenced?.Contains(column.Name) == true,
                Comment: column.Comment))
            .ToList();

        var indexes = table.Indexes
            .OrderBy(index => index.Name, StringComparer.Ordinal)
            .Select(index => new RelationshipIndexView(
                Name: index.Name,
                IsUnique: index.IsUnique,
                Definition: index.Definition,
                Columns: [.. index.Columns]))
            .ToList();

        return new RelationshipTableView(
            Id: id,
            Schema: table.SchemaName,
            Table: table.Name,
            // A live read never counts rows: the count is a scan the user did not ask for (D9).
            RowCount: null,
            ColumnCount: columns.Count,
            IndexCount: indexes.Count,
            ForeignKeyCount: table.ForeignKeys.Count,
            Columns: columns,
            PrimaryKey: primaryKey is null ? null : new RelationshipKeyView(primaryKey.Name, [.. primaryKey.Columns]),
            UniqueKeys:
            [
                .. table.UniqueConstraints
                    .OrderBy(key => key.Name, StringComparer.Ordinal)
                    .Select(key => new RelationshipKeyView(key.Name, [.. key.Columns])),
            ],
            Indexes: indexes,
            CheckConstraints:
            [
                .. table.CheckConstraints
                    .OrderBy(check => check.Name, StringComparer.Ordinal)
                    .Select(check => new RelationshipCheckConstraintView(check.Name, check.Expression)),
            ]);
    }

    private static bool StatesTarget(ForeignKeyDefinition foreignKey) =>
        !string.IsNullOrEmpty(foreignKey.ReferencedTable);

    /// <summary>§5.1: an edge is mandatory when every referencing column is non-nullable.</summary>
    private static bool IsMandatory(
        IReadOnlyDictionary<string, ColumnDefinition> columnsByName,
        IReadOnlyList<string> referencingColumns)
    {
        if (referencingColumns.Count == 0)
        {
            return false;
        }

        foreach (var name in referencingColumns)
        {
            if (!columnsByName.TryGetValue(name, out var column) || column.IsNullable)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// §5.2: <c>NO ACTION</c> is the default the caller never needs to see, so it reads as "not stated"
    /// just as an absent action does.
    /// </summary>
    private static string? NormalizeAction(string? action) =>
        string.IsNullOrWhiteSpace(action)
        || string.Equals(action, "NO ACTION", StringComparison.OrdinalIgnoreCase)
            ? null
            : action;

    private static HashSet<string> GetOrAdd(Dictionary<string, HashSet<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            map[key] = set;
        }

        return set;
    }

    /// <summary>Orders outside targets by schema, then table, both ordinal — an empty schema first.</summary>
    private sealed class OutsideKeyOrder : IComparer<(string Schema, string Table)>
    {
        /// <summary>The one instance: the comparer holds no state.</summary>
        internal static readonly OutsideKeyOrder Instance = new();

        /// <inheritdoc />
        public int Compare((string Schema, string Table) x, (string Schema, string Table) y)
        {
            var schema = string.CompareOrdinal(x.Schema, y.Schema);
            return schema != 0 ? schema : string.CompareOrdinal(x.Table, y.Table);
        }
    }
}
