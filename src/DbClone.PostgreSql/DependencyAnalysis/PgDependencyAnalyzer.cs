using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

using Microsoft.Extensions.Logging;

namespace DbClone.PostgreSql.DependencyAnalysis;

/// <summary>
/// PostgreSQL implementation of <see cref="IDependencyAnalyzer"/>.
/// Uses Kahn's algorithm for topological sorting with cycle detection.
/// </summary>
public sealed class PgDependencyAnalyzer : IDependencyAnalyzer
{
    private readonly ILogger<PgDependencyAnalyzer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgDependencyAnalyzer"/> class.
    /// </summary>
    public PgDependencyAnalyzer(ILogger<PgDependencyAnalyzer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<DependencyResult> AnalyzeAsync(
        DatabaseModel model,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Analyzing dependencies for {TableCount} tables",
            model.Tables.Count);

        var objects = BuildDependencyGraph(model);
        var (ordered, cycles) = TopologicalSort(objects);

        if (cycles.Count > 0)
        {
            _logger.LogWarning("Detected {CycleCount} circular dependencies", cycles.Count);
        }

        _logger.LogInformation(
            "Dependency analysis complete: {OrderedCount} objects ordered",
            ordered.Count);

        return Task.FromResult(new DependencyResult(ordered, cycles));
    }

    /// <summary>
    /// Normalizes a PostgreSQL type notation to a plain type name:
    /// strips array suffixes ("my_type[]"), modifiers ("varchar(50)") and
    /// identifier quotes ("\"MyType\"").
    /// </summary>
    internal static string NormalizeTypeName(string dataType)
    {
        var t = dataType.Trim();

        while (t.EndsWith("[]", StringComparison.Ordinal))
            t = t[..^2].TrimEnd();

        var paren = t.IndexOf('(');
        if (paren > 0)
            t = t[..paren].TrimEnd();

        return string.Join('.', t.Split('.').Select(p => p.Trim().Trim('"')));
    }

    /// <summary>
    /// Attempts to resolve an attribute's data type (in PostgreSQL format_type notation,
    /// e.g. "my_type", "public.my_type", "my_type[]") to a user-defined type in the model.
    /// Unqualified names prefer the owning schema, then fall back to any schema.
    /// </summary>
    internal static bool TryResolveUserType(
        string dataType,
        string ownerSchema,
        Dictionary<string, EDatabaseObjectType> qualifiedKeys,
        Dictionary<string, List<string>> bareKeys,
        out DatabaseObjectReference reference)
    {
        reference = default!;
        var normalized = NormalizeTypeName(dataType);
        if (normalized.Length == 0)
            return false;

        string? qualifiedName = null;
        if (normalized.Contains('.'))
        {
            qualifiedName = normalized;
        }
        else if (bareKeys.TryGetValue(normalized, out var candidates) && candidates.Count > 0)
        {
            qualifiedName = candidates.Find(c => c.StartsWith(
                                ownerSchema + ".",
                                StringComparison.OrdinalIgnoreCase))
                            ?? candidates[0];
        }

        if (qualifiedName is null)
            return false;

        var dot = qualifiedName.IndexOf('.');
        if (dot <= 0 || dot == qualifiedName.Length - 1)
            return false;

        if (!qualifiedKeys.TryGetValue(qualifiedName, out var type))
            return false;

        reference = new DatabaseObjectReference(
            qualifiedName[..dot],
            qualifiedName[(dot + 1)..],
            type);
        return true;
    }

    private static void AddBareKey(
        Dictionary<string, List<string>> keys,
        string name,
        string qualified)
    {
        if (!keys.TryGetValue(name, out var list))
            keys[name] = list = [];
        list.Add(qualified);
    }

    /// <summary>
    /// Builds a lookup of bare type names to their qualified keys, for resolving
    /// unqualified attribute type references.
    /// </summary>
    private static Dictionary<string, List<string>> BuildBareTypeKeys(DatabaseModel model)
    {
        var keys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in model.Enums)
            AddBareKey(keys, e.Name, $"{e.SchemaName}.{e.Name}");
        foreach (var d in model.Domains)
            AddBareKey(keys, d.Name, $"{d.SchemaName}.{d.Name}");
        foreach (var c in model.CompositeTypes)
            AddBareKey(keys, c.Name, $"{c.SchemaName}.{c.Name}");
        return keys;
    }

    private static List<DatabaseObject> BuildDependencyGraph(DatabaseModel model)
    {
        var objects = new List<DatabaseObject>();

        // Add schemas (no dependencies); system schemas are presence-only
        // entries and take no part in the copy dependency graph.
        foreach (var schema in model.Schemas.Where(s => !s.IsSystem))
        {
            objects.Add(
                new DatabaseObject(
                    SchemaName: "",
                    Name: schema.Name,
                    ObjectType: EDatabaseObjectType.Schema,
                    Dependencies: []));
        }

        // Add enum types (depend on schema)
        foreach (var enumDef in model.Enums)
        {
            objects.Add(
                new DatabaseObject(
                    SchemaName: enumDef.SchemaName,
                    Name: enumDef.Name,
                    ObjectType: EDatabaseObjectType.Enum,
                    Dependencies:
                        [
                            new DatabaseObjectReference(
                                enumDef.SchemaName,
                                enumDef.SchemaName,
                                EDatabaseObjectType.Schema)
                        ]));
        }

        // Add domain types (depend on schema)
        foreach (var domain in model.Domains)
        {
            objects.Add(
                new DatabaseObject(
                    SchemaName: domain.SchemaName,
                    Name: domain.Name,
                    ObjectType: EDatabaseObjectType.Domain,
                    Dependencies:
                        [
                            new DatabaseObjectReference(
                                domain.SchemaName,
                                domain.SchemaName,
                                EDatabaseObjectType.Schema)
                        ]));
        }

        // Add composite types (depend on schema and on any user-defined type
        // referenced by an attribute — e.g. a composite containing another
        // composite, enum, or domain, all created within the same stage)
        var userTypeKeys = BuildUserTypeKeys(model);
        var bareTypeKeys = BuildBareTypeKeys(model);
        foreach (var ct in model.CompositeTypes)
        {
            var deps = new List<DatabaseObjectReference>
                           {
                               new(ct.SchemaName, ct.SchemaName, EDatabaseObjectType.Schema)
                           };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var attr in ct.Attributes)
            {
                if (TryResolveUserType(
                        attr.DataType,
                        ct.SchemaName,
                        userTypeKeys,
                        bareTypeKeys,
                        out var typeRef)
                    && !(string.Equals(
                             typeRef.SchemaName,
                             ct.SchemaName,
                             StringComparison.OrdinalIgnoreCase)
                         && string.Equals(
                             typeRef.Name,
                             ct.Name,
                             StringComparison.OrdinalIgnoreCase))
                    && seen.Add($"{typeRef.ObjectType}:{typeRef.SchemaName}.{typeRef.Name}"))
                {
                    deps.Add(typeRef);
                }
            }

            objects.Add(
                new DatabaseObject(
                    SchemaName: ct.SchemaName,
                    Name: ct.Name,
                    ObjectType: EDatabaseObjectType.CompositeType,
                    Dependencies: deps));
        }

        // Add sequences (depend on schema)
        foreach (var seq in model.Sequences)
        {
            objects.Add(
                new DatabaseObject(
                    SchemaName: seq.SchemaName,
                    Name: seq.Name,
                    ObjectType: EDatabaseObjectType.Sequence,
                    Dependencies:
                        [
                            new DatabaseObjectReference(
                                seq.SchemaName,
                                seq.SchemaName,
                                EDatabaseObjectType.Schema)
                        ]));
        }

        // Add tables (depend on schema, enum types used in columns, referenced tables via FK)
        foreach (var table in model.Tables)
        {
            var deps = new List<DatabaseObjectReference>
                           {
                               new(table.SchemaName, table.SchemaName, EDatabaseObjectType.Schema)
                           };

            // FK dependencies
            foreach (var fk in table.ForeignKeys)
            {
                if (fk.ReferencedSchema != table.SchemaName || fk.ReferencedTable != table.Name)
                {
                    deps.Add(
                        new DatabaseObjectReference(
                            fk.ReferencedSchema,
                            fk.ReferencedTable,
                            EDatabaseObjectType.Table));
                }
            }

            objects.Add(
                new DatabaseObject(
                    SchemaName: table.SchemaName,
                    Name: table.Name,
                    ObjectType: EDatabaseObjectType.Table,
                    Dependencies: deps));
        }

        // Add views (depend on schema and on the relations they read from —
        // a view selecting from another view must be created after it)
        var viewKeys = new HashSet<string>(
            model.Views.Select(v => $"{v.SchemaName}.{v.Name}"),
            StringComparer.OrdinalIgnoreCase);
        var matViewKeys = new HashSet<string>(
            model.MaterializedViews.Select(v => $"{v.SchemaName}.{v.Name}"),
            StringComparer.OrdinalIgnoreCase);
        var tableKeys = new HashSet<string>(
            model.Tables.Select(t => $"{t.SchemaName}.{t.Name}"),
            StringComparer.OrdinalIgnoreCase);
        foreach (var view in model.Views)
        {
            var deps = new List<DatabaseObjectReference>
                           {
                               new(view.SchemaName, view.SchemaName, EDatabaseObjectType.Schema)
                           };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rel in view.ReferencedRelations ?? [])
            {
                var dot = rel.IndexOf('.');
                if (dot <= 0 || dot == rel.Length - 1)
                    continue;

                var schema = rel[..dot];
                var name = rel[(dot + 1)..];
                if (string.Equals(schema, view.SchemaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(name, view.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                EDatabaseObjectType? refType =
                    viewKeys.Contains(rel) ? EDatabaseObjectType.View :
                    matViewKeys.Contains(rel) ? EDatabaseObjectType.MaterializedView :
                    tableKeys.Contains(rel) ? EDatabaseObjectType.Table :
                    null;

                if (refType is not null && seen.Add($"{refType}:{rel}"))
                    deps.Add(new DatabaseObjectReference(schema, name, refType.Value));
            }

            objects.Add(
                new DatabaseObject(
                    SchemaName: view.SchemaName,
                    Name: view.Name,
                    ObjectType: EDatabaseObjectType.View,
                    Dependencies: deps));
        }

        // Add materialized views (depend on schema)
        foreach (var mv in model.MaterializedViews)
        {
            objects.Add(
                new DatabaseObject(
                    SchemaName: mv.SchemaName,
                    Name: mv.Name,
                    ObjectType: EDatabaseObjectType.MaterializedView,
                    Dependencies:
                        [
                            new DatabaseObjectReference(
                                mv.SchemaName,
                                mv.SchemaName,
                                EDatabaseObjectType.Schema)
                        ]));
        }

        // Add functions (depend on schema)
        foreach (var func in model.Functions)
        {
            objects.Add(
                new DatabaseObject(
                    SchemaName: func.SchemaName,
                    Name: func.Name,
                    ObjectType: EDatabaseObjectType.Function,
                    Dependencies:
                        [
                            new DatabaseObjectReference(
                                func.SchemaName,
                                func.SchemaName,
                                EDatabaseObjectType.Schema)
                        ]));
        }

        // Add triggers (depend on table and function)
        foreach (var trigger in model.Triggers)
        {
            var deps = new List<DatabaseObjectReference>
                           {
                               new(
                                   trigger.SchemaName,
                                   trigger.TableName,
                                   EDatabaseObjectType.Table),
                               new(
                                   trigger.FunctionSchema,
                                   trigger.FunctionName,
                                   EDatabaseObjectType.Function)
                           };

            objects.Add(
                new DatabaseObject(
                    SchemaName: trigger.SchemaName,
                    Name: trigger.Name,
                    ObjectType: EDatabaseObjectType.Trigger,
                    Dependencies: deps));
        }

        return objects;
    }

    /// <summary>
    /// Builds a lookup of all user-defined types (enums, domains, composite types)
    /// keyed by "schema.name".
    /// </summary>
    private static Dictionary<string, EDatabaseObjectType> BuildUserTypeKeys(DatabaseModel model)
    {
        var keys = new Dictionary<string, EDatabaseObjectType>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in model.Enums)
            keys[$"{e.SchemaName}.{e.Name}"] = EDatabaseObjectType.Enum;
        foreach (var d in model.Domains)
            keys[$"{d.SchemaName}.{d.Name}"] = EDatabaseObjectType.Domain;
        foreach (var c in model.CompositeTypes)
            keys[$"{c.SchemaName}.{c.Name}"] = EDatabaseObjectType.CompositeType;
        return keys;
    }

    private static string MakeKey(DatabaseObject obj) =>
        $"{obj.ObjectType}:{obj.SchemaName}.{obj.Name}";

    private static string MakeKey(DatabaseObjectReference objRef) =>
        $"{objRef.ObjectType}:{objRef.SchemaName}.{objRef.Name}";

    private (IReadOnlyList<DatabaseObject> Ordered, IReadOnlyList<CircularDependency> Cycles)
        TopologicalSort(List<DatabaseObject> objects)
    {
        // Build adjacency structures
        var keyMap = new Dictionary<string, DatabaseObject>();
        var inDegree = new Dictionary<string, int>();
        var adjacency = new Dictionary<string, List<string>>();

        foreach (var obj in objects)
        {
            var key = MakeKey(obj);
            keyMap.TryAdd(key, obj);
            inDegree.TryAdd(key, 0);
            adjacency.TryAdd(key, []);
        }

        // Build edges (dependency -> dependent)
        foreach (var obj in objects)
        {
            var objKey = MakeKey(obj);
            foreach (var dep in obj.Dependencies)
            {
                var depKey = MakeKey(dep);
                if (adjacency.ContainsKey(depKey))
                {
                    adjacency[depKey].Add(objKey);
                    inDegree[objKey] = inDegree.GetValueOrDefault(objKey, 0) + 1;
                }
            }
        }

        // Kahn's algorithm
        var queue = new Queue<string>();
        foreach (var (key, degree) in inDegree)
        {
            if (degree == 0)
                queue.Enqueue(key);
        }

        var ordered = new List<DatabaseObject>();
        while (queue.Count > 0)
        {
            var key = queue.Dequeue();
            if (keyMap.TryGetValue(key, out var obj))
                ordered.Add(obj);

            if (adjacency.TryGetValue(key, out var dependents))
            {
                foreach (var dependent in dependents)
                {
                    inDegree[dependent]--;
                    if (inDegree[dependent] == 0)
                        queue.Enqueue(dependent);
                }
            }
        }

        // Detect cycles: any objects not in ordered list
        var cycles = new List<CircularDependency>();
        var remaining = keyMap.Keys.Where(k => !ordered.Any(o => MakeKey(o) == k)).ToList();

        if (remaining.Count > 0)
        {
            var cycleRefs = remaining.Select(k =>
                {
                    if (keyMap.TryGetValue(k, out var obj))
                        return new DatabaseObjectReference(
                            obj.SchemaName,
                            obj.Name,
                            obj.ObjectType);
                    return new DatabaseObjectReference("", k, EDatabaseObjectType.Table);
                }).ToList();

            cycles.Add(
                new CircularDependency(
                    Cycle: cycleRefs,
                    Description:
                    $"Circular dependency detected among {remaining.Count} objects: {string.Join(", ", remaining)}"));
        }

        return (ordered, cycles);
    }
}
