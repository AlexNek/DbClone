using CanonicalModel.Interchange;
using CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Interchange;

/// <summary>
/// Resolves content-derived <see cref="InterchangeId"/> values for the objects of one database
/// model, so parent references exist before the children that must point at them are written.
/// <para>
/// Identity is positional: kind, schema name, object name and parent id, hashed by
/// <see cref="InterchangeId.Compute"/>. Names alone are not unique across PostgreSQL kinds (a
/// table and a view may share one), so the kind token is part of every computation here and the
/// lookup keys are qualified.
/// </para>
/// </summary>
internal sealed class InterchangeIdGraph
{
    private const string TableColumnSeparator = "_";

    private readonly Dictionary<string, InterchangeId> _schemas = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), InterchangeId> _tables = new();
    private readonly Dictionary<(string, string), InterchangeId> _views = new();
    private readonly Dictionary<string, InterchangeId> _relations = new(StringComparer.Ordinal);

    /// <summary>
    /// Routine ids grouped by (schema, unqualified name). PostgreSQL allows overloads, so one name
    /// can own several ids; a caller that knows the signature gets the exact one, and a caller that
    /// does not gets the single match when there is only one.
    /// </summary>
    private readonly Dictionary<(string, string), List<InterchangeId>> _routines = new();

    private InterchangeIdGraph()
    {
    }

    /// <summary>
    /// Builds the graph for <paramref name="model"/> in a deterministic order (schema, then object
    /// name), which is what makes the same database state produce the same ids on every run.
    /// </summary>
    public static InterchangeIdGraph Create(DatabaseModel model, string dialect)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(dialect);

        var graph = new InterchangeIdGraph();

        foreach (var schema in model.Schemas.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            graph._schemas[schema.Name] = InterchangeId.Compute(
                "schema",
                string.Empty,
                schema.Name);
        }

        foreach (var table in model.Tables
                     .OrderBy(t => t.SchemaName, StringComparer.Ordinal)
                     .ThenBy(t => t.Name, StringComparer.Ordinal))
        {
            var id = InterchangeId.Compute(
                "table",
                table.SchemaName,
                table.Name,
                graph.Schema(table.SchemaName));
            graph._tables[(table.SchemaName, table.Name)] = id;
            graph._relations[$"{table.SchemaName}.{table.Name}"] = id;
        }

        // Views and materialized views are separate lists in the model and share nothing but
        // their naming, so both are folded into one id space keyed by (schema, name).
        var viewNames = model.Views
            .Select(v => (v.SchemaName, v.Name))
            .Concat(model.MaterializedViews.Select(v => (v.SchemaName, v.Name)))
            .OrderBy(v => v.SchemaName, StringComparer.Ordinal)
            .ThenBy(v => v.Name, StringComparer.Ordinal);

        foreach (var (schemaName, viewName) in viewNames)
        {
            var id = InterchangeId.Compute(
                "view",
                schemaName,
                viewName,
                graph.Schema(schemaName));
            graph._views[(schemaName, viewName)] = id;
            graph._relations[$"{schemaName}.{viewName}"] = id;
        }

        foreach (var function in model.Functions
                     .OrderBy(f => f.SchemaName, StringComparer.Ordinal)
                     .ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            var signature = string.Join(
                ",",
                function.Parameters.Select(p => p.DataType.ToLowerInvariant()));
            var id = InterchangeId.Compute(
                "routine",
                function.SchemaName,
                $"{function.Name}({signature})",
                graph.Schema(function.SchemaName));

            if (!graph._routines.TryGetValue((function.SchemaName, function.Name), out var list))
            {
                list = [];
                graph._routines[(function.SchemaName, function.Name)] = list;
            }

            list.Add(id);
        }

        // Two overloads of one name sort equal in the catalog query, so their order is not
        // guaranteed by the server. Sorting each group makes the chosen reference reproducible.
        foreach (var list in graph._routines.Values.Where(list => list.Count > 1))
        {
            list.Sort();
        }

        return graph;
    }

    /// <summary>Gets the id of a schema.</summary>
    public InterchangeId Schema(string schemaName) =>
        _schemas.TryGetValue(schemaName, out var id)
            ? id
            // A schema missing from model.Schemas (created by an extension, for example) still
            // needs a stable id, so it is computed from the same inputs rather than guessed.
            : InterchangeId.Compute("schema", string.Empty, schemaName);

    /// <summary>Gets the id of a table.</summary>
    public InterchangeId Table(string schemaName, string tableName) =>
        _tables.TryGetValue((schemaName, tableName), out var id)
            ? id
            : InterchangeId.Compute("table", schemaName, tableName, Schema(schemaName));

    /// <summary>Gets the id of a column, identified through its table.</summary>
    public InterchangeId Column(
        string schemaName,
        string tableName,
        string columnName,
        InterchangeId tableId) =>
        InterchangeId.Compute(
            "column",
            schemaName,
            $"{tableName}{TableColumnSeparator}{columnName}",
            tableId);

    /// <summary>Gets the id of a primary key constraint.</summary>
    public InterchangeId PrimaryKey(
        string schemaName,
        string tableName,
        string constraintName,
        InterchangeId tableId) =>
        InterchangeId.Compute("primaryKey", schemaName, $"{tableName}_{constraintName}", tableId);

    /// <summary>Gets the id of a foreign key constraint.</summary>
    public InterchangeId ForeignKey(
        string schemaName,
        string tableName,
        string constraintName,
        InterchangeId tableId) =>
        InterchangeId.Compute("foreignKey", schemaName, $"{tableName}_{constraintName}", tableId);

    /// <summary>Gets the id of a unique constraint.</summary>
    public InterchangeId UniqueConstraint(
        string schemaName,
        string tableName,
        string constraintName,
        InterchangeId tableId) =>
        InterchangeId.Compute("uniqueConstraint", schemaName, $"{tableName}_{constraintName}", tableId);

    /// <summary>Gets the id of a check constraint.</summary>
    public InterchangeId CheckConstraint(
        string schemaName,
        string tableName,
        string constraintName,
        InterchangeId tableId) =>
        InterchangeId.Compute("checkConstraint", schemaName, $"{tableName}_{constraintName}", tableId);

    /// <summary>Gets the id of an index.</summary>
    public InterchangeId Index(
        string schemaName,
        string tableName,
        string indexName,
        InterchangeId tableId) =>
        InterchangeId.Compute("index", schemaName, $"{tableName}_{indexName}", tableId);

    /// <summary>Gets the id of a trigger, which lives under its table.</summary>
    public InterchangeId Trigger(string schemaName, string tableName, string triggerName) =>
        InterchangeId.Compute(
            "trigger",
            schemaName,
            $"{tableName}_{triggerName}",
            Table(schemaName, tableName));

    /// <summary>Gets the id of a view or materialized view.</summary>
    public InterchangeId View(string schemaName, string viewName) =>
        _views.TryGetValue((schemaName, viewName), out var id)
            ? id
            : InterchangeId.Compute("view", schemaName, viewName, Schema(schemaName));

    /// <summary>
    /// Gets the id of a routine. <paramref name="signature"/> is the comma-joined lowercase
    /// parameter type list when the caller knows it; passing null asks for the routine of that
    /// name, which resolves only while the name has exactly one overload.
    /// </summary>
    public InterchangeId Routine(string schemaName, string routineName, string? signature)
    {
        if (signature is not null)
        {
            return InterchangeId.Compute(
                "routine",
                schemaName,
                $"{routineName}({signature})",
                Schema(schemaName));
        }

        // A caller without the signature (a trigger names its function by name alone) gets the
        // overload captured under that name; when several exist, the first in sorted order keeps
        // the reference stable across runs.
        return _routines.TryGetValue((schemaName, routineName), out var ids) && ids.Count > 0
                   ? ids[0]
                   : InterchangeId.Compute(
                         "routine",
                         schemaName,
                         $"{routineName}()",
                         Schema(schemaName));
    }

    /// <summary>Gets the id of a sequence.</summary>
    public InterchangeId Sequence(string schemaName, string sequenceName) =>
        InterchangeId.Compute("sequence", schemaName, sequenceName, Schema(schemaName));

    /// <summary>Gets the id of an extension, which is database-wide and not schema-scoped.</summary>
    public InterchangeId Extension(string extensionName) =>
        InterchangeId.Compute("extension", string.Empty, extensionName);

    /// <summary>
    /// Gets the id of a type — an enum, a composite type or a domain, the kinds the neutral model has
    /// no structured home for and that travel as native text.
    /// </summary>
    /// <remarks>
    /// One token covers all three because PostgreSQL stores all three in <c>pg_type</c>, where a name
    /// is unique per schema: an enum and a domain cannot both be called <c>mood</c> in one schema, so
    /// the kind adds nothing to the identity that the schema and the name do not already settle.
    /// </remarks>
    public InterchangeId Type(string schemaName, string typeName) =>
        InterchangeId.Compute("type", schemaName, typeName, Schema(schemaName));

    /// <summary>Gets the id of a publication, which is database-wide and not schema-scoped.</summary>
    public InterchangeId Publication(string publicationName) =>
        InterchangeId.Compute("publication", string.Empty, publicationName);

    /// <summary>Gets the id of a subscription, which is database-wide and not schema-scoped.</summary>
    public InterchangeId Subscription(string subscriptionName) =>
        InterchangeId.Compute("subscription", string.Empty, subscriptionName);

    /// <summary>Gets the id of an RLS policy.</summary>
    public InterchangeId Policy(string schemaName, string policyName) =>
        InterchangeId.Compute("policy", schemaName, policyName, Schema(schemaName));

    /// <summary>
    /// Resolves the <c>schema.name</c> strings the metadata reader reports as referenced relations
    /// to the ids of the objects they name. Names that match no captured object are skipped: the
    /// dependency graph may only point at objects the catalog actually contains.
    /// </summary>
    public IEnumerable<InterchangeId> ResolveRelations(IEnumerable<string> qualifiedNames)
    {
        var seen = new HashSet<InterchangeId>();
        foreach (var name in qualifiedNames.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (_relations.TryGetValue(name, out var id) && seen.Add(id))
            {
                yield return id;
            }
        }
    }
}
