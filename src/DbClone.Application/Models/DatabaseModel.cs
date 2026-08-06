namespace DbClone.Application.Models;

/// <summary>
/// Root aggregate representing a complete database model.
/// </summary>
public sealed record DatabaseModel(
    string DatabaseName,
    string ServerVersion,
    IReadOnlyList<SchemaDefinition> Schemas,
    IReadOnlyList<TableDefinition> Tables,
    IReadOnlyList<ViewDefinition> Views,
    IReadOnlyList<MaterializedViewDefinition> MaterializedViews,
    IReadOnlyList<SequenceDefinition> Sequences,
    IReadOnlyList<EnumDefinition> Enums,
    IReadOnlyList<DomainDefinition> Domains,
    IReadOnlyList<CompositeTypeDefinition> CompositeTypes,
    IReadOnlyList<FunctionDefinition> Functions,
    IReadOnlyList<TriggerDefinition> Triggers,
    IReadOnlyList<PolicyDefinition> Policies,
    IReadOnlyList<PublicationDefinition> Publications,
    IReadOnlyList<SubscriptionDefinition> Subscriptions,
    IReadOnlyList<ExtensionDefinition> Extensions)
{
    /// <summary>
    /// Returns a copy of this model with all objects belonging to the specified
    /// schemas removed. Convenience inverse of <see cref="FilterToSchemas"/>.
    /// </summary>
    public DatabaseModel ExcludeSchemas(HashSet<string> excluded)
    {
        return this with
                   {
                       Schemas = [.. Schemas.Where(s => !excluded.Contains(s.Name))],
                       Tables = [.. Tables.Where(t => !excluded.Contains(t.SchemaName))],
                       Views = [.. Views.Where(v => !excluded.Contains(v.SchemaName))],
                       MaterializedViews =
                           [.. MaterializedViews.Where(v => !excluded.Contains(v.SchemaName))],
                       Sequences = [.. Sequences.Where(s => !excluded.Contains(s.SchemaName))],
                       Enums = [.. Enums.Where(e => !excluded.Contains(e.SchemaName))],
                       Domains = [.. Domains.Where(d => !excluded.Contains(d.SchemaName))],
                       CompositeTypes =
                           [.. CompositeTypes.Where(t => !excluded.Contains(t.SchemaName))],
                       Functions = [.. Functions.Where(f => !excluded.Contains(f.SchemaName))],
                       Triggers = [.. Triggers.Where(t => !excluded.Contains(t.SchemaName))],
                       Policies = [.. Policies.Where(p => !excluded.Contains(p.SchemaName))]
                   };
    }

    /// <summary>
    /// Returns a copy of this model containing only objects belonging to the
    /// specified schemas. Used by both the copy pipeline (exclude non-writable
    /// schemas) and the comparison feature (exclude non-readable schemas).
    /// </summary>
    public DatabaseModel FilterToSchemas(HashSet<string> schemas)
    {
        return this with
                   {
                       Schemas = [.. Schemas.Where(s => schemas.Contains(s.Name))],
                       Tables = [.. Tables.Where(t => schemas.Contains(t.SchemaName))],
                       Views = [.. Views.Where(v => schemas.Contains(v.SchemaName))],
                       MaterializedViews =
                           [.. MaterializedViews.Where(v => schemas.Contains(v.SchemaName))],
                       Sequences = [.. Sequences.Where(s => schemas.Contains(s.SchemaName))],
                       Enums = [.. Enums.Where(e => schemas.Contains(e.SchemaName))],
                       Domains = [.. Domains.Where(d => schemas.Contains(d.SchemaName))],
                       CompositeTypes =
                           [.. CompositeTypes.Where(t => schemas.Contains(t.SchemaName))],
                       Functions = [.. Functions.Where(f => schemas.Contains(f.SchemaName))],
                       Triggers = [.. Triggers.Where(t => schemas.Contains(t.SchemaName))],
                       Policies = [.. Policies.Where(p => schemas.Contains(p.SchemaName))]
                   };
    }
}
