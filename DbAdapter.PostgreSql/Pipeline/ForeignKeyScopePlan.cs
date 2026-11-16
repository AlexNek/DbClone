using CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Pipeline;

/// <summary>
/// The split of a model's foreign keys by the one scope test: a constraint may be created
/// only while the table it references is itself in the copied scope. The parent must be in
/// the model (which is why an excluded schema needs no separate test — the model no longer
/// holds its tables) and must not have failed to be created.
/// </summary>
/// <param name="InScope">The constraints to create, grouped by the child table that stores them.</param>
/// <param name="Deferred">One entry per parent table outside the scope, ordered by schema then name.</param>
public sealed record ForeignKeyScopePlan(
    IReadOnlyList<InScopeForeignKeys> InScope,
    IReadOnlyList<DeferredForeignKeyGroup> Deferred)
{
    /// <summary>Number of constraints the copy may create.</summary>
    public int InScopeCount => InScope.Sum(group => group.ForeignKeys.Count);

    /// <summary>Number of constraints deferred because their parent table is outside the copied scope.</summary>
    public int DeferredCount => Deferred.Sum(group => group.Count);

    /// <summary>
    /// Splits the model's foreign keys once per stage run. Pure: same model and skip set,
    /// same plan, no database access.
    /// </summary>
    public static ForeignKeyScopePlan Build(
        DatabaseModel model,
        IReadOnlySet<TableId> skippedTables)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(skippedTables);

        var inScope = new List<InScopeForeignKeys>(model.Tables.Count);
        var deferredByParent = new Dictionary<TableId, int>();

        foreach (var table in model.Tables)
        {
            if (table.ForeignKeys.Count == 0)
                continue;

            var keep = new List<ForeignKeyDefinition>(table.ForeignKeys.Count);

            foreach (var fk in table.ForeignKeys)
            {
                var parent = new TableId(fk.ReferencedSchema, fk.ReferencedTable);

                if (model.ContainsTable(fk.ReferencedSchema, fk.ReferencedTable)
                    && !skippedTables.Contains(parent))
                {
                    keep.Add(fk);
                }
                else
                {
                    deferredByParent[parent] = deferredByParent.GetValueOrDefault(parent) + 1;
                }
            }

            if (keep.Count > 0)
                inScope.Add(new InScopeForeignKeys(new TableId(table.SchemaName, table.Name), keep));
        }

        var deferred = deferredByParent
            .OrderBy(entry => entry.Key.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new DeferredForeignKeyGroup(entry.Key, entry.Value))
            .ToList();

        return new ForeignKeyScopePlan(inScope, deferred);
    }
}
