using CanonicalModel.Models;

namespace Application.Tests.Relationship;

/// <summary>
/// Builds a <see cref="DatabaseModel"/> the way an adapter hands one over: schema and table names as
/// the engine states them, a primary key expressed as the index the model marks primary, and foreign
/// keys that name their target in engine-native strings. Nothing here is a real host, user or database.
/// </summary>
internal static class RelationshipModelFixture
{
    /// <summary>Wraps the given tables in an otherwise empty model.</summary>
    /// <param name="tables">The tables the model holds.</param>
    /// <returns>The model.</returns>
    internal static DatabaseModel Model(params TableDefinition[] tables) =>
        new(
            DatabaseName: "testdb",
            ServerVersion: "16.0",
            Schemas: [],
            Tables: tables,
            Views: [],
            MaterializedViews: [],
            Sequences: [],
            Enums: [],
            Domains: [],
            CompositeTypes: [],
            Functions: [],
            Triggers: [],
            Policies: [],
            Publications: [],
            Subscriptions: [],
            Extensions: []);

    /// <summary>Declares a table.</summary>
    internal static TableDefinition Table(
        string schema,
        string name,
        IReadOnlyList<ColumnDefinition>? columns = null,
        IReadOnlyList<IndexDefinition>? indexes = null,
        IReadOnlyList<ForeignKeyDefinition>? foreignKeys = null,
        IReadOnlyList<CheckConstraintDefinition>? checks = null,
        IReadOnlyList<UniqueConstraintDefinition>? uniqueConstraints = null) =>
        new(
            SchemaName: schema,
            Name: name,
            Columns: columns ?? [],
            Indexes: indexes ?? [],
            ForeignKeys: foreignKeys ?? [],
            CheckConstraints: checks ?? [],
            UniqueConstraints: uniqueConstraints ?? [],
            Comment: null,
            IsPartitioned: false,
            PartitionStrategy: null,
            ParentTable: null);

    /// <summary>Declares a column at an ordinal position.</summary>
    internal static ColumnDefinition Column(
        string name,
        int ordinal,
        bool nullable = false,
        string dataType = "integer",
        string? comment = null) =>
        new(
            Name: name,
            DataType: dataType,
            OrdinalPosition: ordinal,
            IsNullable: nullable,
            DefaultValue: null,
            IsIdentity: false,
            IsGenerated: false,
            GenerationExpression: null,
            Comment: comment);

    /// <summary>Declares the primary key as the index the model marks primary.</summary>
    internal static IndexDefinition PrimaryIndex(params string[] columns) =>
        new(Name: "test_pkey", Columns: columns, IsUnique: true, IsPrimary: true, FilterExpression: null, Tablespace: null);

    /// <summary>Declares an index, stating its definition only when one is given.</summary>
    internal static IndexDefinition Index(string name, bool unique, string[] columns, string? definition = null) =>
        new(Name: name, Columns: columns, IsUnique: unique, IsPrimary: false, FilterExpression: null, Tablespace: null, Definition: definition);

    /// <summary>Declares a foreign key that names its target.</summary>
    internal static ForeignKeyDefinition ForeignKey(
        string name,
        string referencedSchema,
        string referencedTable,
        string[] columns,
        string[] referencedColumns,
        string deleteRule = "NO ACTION",
        string updateRule = "NO ACTION") =>
        new(
            Name: name,
            Columns: columns,
            ReferencedSchema: referencedSchema,
            ReferencedTable: referencedTable,
            ReferencedColumns: referencedColumns,
            UpdateRule: updateRule,
            DeleteRule: deleteRule,
            IsDeferrable: false,
            IsInitiallyDeferred: false);

    /// <summary>Declares a foreign key that states no target at all.</summary>
    internal static ForeignKeyDefinition UnresolvedForeignKey(string name, string[] columns) =>
        ForeignKey(name, string.Empty, string.Empty, columns, []);

    /// <summary>Declares a unique constraint.</summary>
    internal static UniqueConstraintDefinition Unique(string name, params string[] columns) =>
        new(Name: name, Columns: columns, IsDeferrable: false, IsInitiallyDeferred: false);

    /// <summary>Declares a check constraint.</summary>
    internal static CheckConstraintDefinition Check(string name, string expression) =>
        new(Name: name, Expression: expression, IsDeferrable: false, IsInitiallyDeferred: false);
}
