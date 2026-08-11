using DbClone.Application.Interfaces;
using DbClone.Application.Models;

namespace DbClone.Application.TableFilter;

/// <summary>
/// Resolves a <see cref="TableSelectionSpec"/> against a
/// <see cref="DatabaseModel"/>. Removes excluded tables together with their
/// table-owned objects (indexes, constraints, triggers, policies, owned sequences),
/// strips foreign keys referencing excluded tables, and skips views whose
/// dependencies include an excluded table. All decisions are collected in a
/// <see cref="TableFilterReport"/> so callers can surface warnings.
/// Stateless — safe to register as a singleton.
/// </summary>
public sealed class TableFilterApplier : ITableFilterApplier
{
    /// <inheritdoc />
    public TableFilterResult Apply(DatabaseModel model, TableSelectionSpec? spec)
    {
        if (spec is not { IsActive: true })
        {
            return new TableFilterResult(model, TableFilterReport.Empty);
        }

        var excluded = spec.ExcludedTables;

        // Tables — excluded parents also remove their partitions (partitions cannot
        // exist without the parent, so they are skipped and reported as orphaned).
        var removedTables = new List<TableId>();
        var orphanedPartitions = new List<TableId>();
        var keptTables = new List<TableDefinition>(model.Tables.Count);

        foreach (var table in model.Tables)
        {
            var id = new TableId(table.SchemaName, table.Name);

            if (excluded.Contains(id))
            {
                removedTables.Add(id);
                continue;
            }

            if (table.ParentTable is not null
                && excluded.Contains(ParseQualified(table.ParentTable)))
            {
                orphanedPartitions.Add(id);
                continue;
            }

            keptTables.Add(table);
        }

        // Foreign keys — strip constraints that would dangle against excluded tables.
        var droppedForeignKeys = new List<DroppedForeignKey>();
        var tablesWithFks = new List<TableDefinition>(keptTables.Count);

        foreach (var table in keptTables)
        {
            var dangling = table.ForeignKeys
                .Where(fk => excluded.Contains(new TableId(fk.ReferencedSchema, fk.ReferencedTable)))
                .ToList();

            if (dangling.Count == 0)
            {
                tablesWithFks.Add(table);
                continue;
            }

            var ownerId = new TableId(table.SchemaName, table.Name);

            foreach (var fk in dangling)
            {
                droppedForeignKeys.Add(
                    new DroppedForeignKey(
                        ownerId,
                        fk.Name,
                        new TableId(fk.ReferencedSchema, fk.ReferencedTable)));
            }

            tablesWithFks.Add(
                table with { ForeignKeys = table.ForeignKeys.Except(dangling).ToList() });
        }

        // Views — skip views whose declared dependencies include an excluded table.
        // Best effort: ReferencedRelations is dot-joined metadata from pg_depend.
        var skippedViews = new List<TableId>();
        var keptViews = new List<ViewDefinition>(model.Views.Count);

        foreach (var view in model.Views)
        {
            if (ReferencesExcludedTable(view.ReferencedRelations, excluded))
            {
                skippedViews.Add(new TableId(view.SchemaName, view.Name));
                continue;
            }

            keptViews.Add(view);
        }

        // Sequences — identity/serial backing sequences owned by excluded tables go
        // with the table; standalone sequences are table-independent and stay.
        var keptSequences = model.Sequences
            .Where(s => s.OwnerTable is null || !excluded.Contains(ParseQualified(s.OwnerTable)))
            .ToList();

        // Triggers and policies are table-owned objects and go with their table.
        var keptTriggers = model.Triggers
            .Where(t => !excluded.Contains(new TableId(t.SchemaName, t.TableName)))
            .ToList();

        var keptPolicies = model.Policies
            .Where(p => !excluded.Contains(new TableId(p.SchemaName, p.TableName)))
            .ToList();

        // Materialized views carry no dependency metadata in the current model —
        // they are kept and may fail in the CreateViews stage (documented limitation).

        var report = new TableFilterReport(
            RemovedTables: removedTables,
            StaleExclusions: [.. excluded.Where(id => !removedTables.Contains(id))],
            DroppedForeignKeys: droppedForeignKeys,
            SkippedViews: skippedViews,
            OrphanedPartitions: orphanedPartitions);

        var filteredModel = model with
        {
            Tables = tablesWithFks,
            Views = keptViews,
            Sequences = keptSequences,
            Triggers = keptTriggers,
            Policies = keptPolicies
        };

        return new TableFilterResult(filteredModel, report);
    }

    /// <inheritdoc />
    public TableId ParseQualified(string qualifiedName)
    {
        var separatorIndex = qualifiedName.IndexOf('.');

        return separatorIndex <= 0 || separatorIndex == qualifiedName.Length - 1
            ? new TableId(string.Empty, qualifiedName)
            : new TableId(qualifiedName[..separatorIndex], qualifiedName[(separatorIndex + 1)..]);
    }

    private bool ReferencesExcludedTable(
        IReadOnlyList<string>? referencedRelations,
        IReadOnlySet<TableId> excluded)
    {
        if (referencedRelations is null)
        {
            return false;
        }

        return referencedRelations.Any(r => excluded.Contains(ParseQualified(r)));
    }
}
