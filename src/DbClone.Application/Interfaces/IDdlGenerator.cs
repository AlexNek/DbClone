using DbClone.Application.Models;

namespace DbClone.Application.Interfaces;

/// <summary>
/// Generates DDL statements from database models.
/// </summary>
public interface IDdlGenerator
{
    /// <summary>
    /// Generates CREATE statements for composite types.
    /// </summary>
    IReadOnlyList<string> GenerateCreateCompositeTypes(IEnumerable<CompositeTypeDefinition> types);

    /// <summary>
    /// Generates CREATE statements for domain types.
    /// </summary>
    IReadOnlyList<string> GenerateCreateDomains(IEnumerable<DomainDefinition> domains);

    /// <summary>
    /// Generates CREATE statements for enum types.
    /// </summary>
    IReadOnlyList<string> GenerateCreateEnums(IEnumerable<EnumDefinition> enums);

    /// <summary>
    /// Generates CREATE statements for functions.
    /// </summary>
    IReadOnlyList<string> GenerateCreateFunctions(IEnumerable<FunctionDefinition> functions);

    /// <summary>
    /// Generates CREATE statements for indexes.
    /// </summary>
    IReadOnlyList<string> GenerateCreateIndexes(
        IEnumerable<IndexDefinition> indexes,
        string schemaName,
        string tableName);

    /// <summary>
    /// Generates CREATE statements for materialized views.
    /// </summary>
    IReadOnlyList<string> GenerateCreateMaterializedViews(
        IEnumerable<MaterializedViewDefinition> views);

    /// <summary>
    /// Generates CREATE statements for schemas.
    /// </summary>
    IReadOnlyList<string> GenerateCreateSchemas(IEnumerable<SchemaDefinition> schemas);

    /// <summary>
    /// Generates CREATE statements for sequences.
    /// </summary>
    IReadOnlyList<string> GenerateCreateSequences(IEnumerable<SequenceDefinition> sequences);

    /// <summary>
    /// Generates CREATE statements for tables (without foreign keys).
    /// </summary>
    IReadOnlyList<string> GenerateCreateTables(IEnumerable<TableDefinition> tables);

    /// <summary>
    /// Generates CREATE statements for triggers.
    /// </summary>
    IReadOnlyList<string> GenerateCreateTriggers(IEnumerable<TriggerDefinition> triggers);

    /// <summary>
    /// Generates CREATE statements for views.
    /// </summary>
    IReadOnlyList<string> GenerateCreateViews(IEnumerable<ViewDefinition> views);

    /// <summary>
    /// Generates ALTER statements for foreign keys.
    /// </summary>
    IReadOnlyList<string> GenerateForeignKeys(
        IEnumerable<ForeignKeyDefinition> foreignKeys,
        string schemaName,
        string tableName);

    /// <summary>
    /// Generates setval statements for sequence synchronization.
    /// </summary>
    string GenerateSetSequenceValue(string schemaName, string sequenceName, long value);
}
