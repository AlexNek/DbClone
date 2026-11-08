using System.Text.Json;

namespace CanonicalModel.Interchange;

/// <summary>
/// Table kind classification.
/// </summary>
public enum ETableKind
{
    /// <summary>Regular table.</summary>
    Table,

    /// <summary>Partitioned table.</summary>
    Partitioned,

    /// <summary>Foreign table.</summary>
    Foreign,

    /// <summary>Temporary table.</summary>
    Temporary,
}

/// <summary>
/// A table in the interchange model.
/// </summary>
public sealed record TableDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.Table;

    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public string? SourceObjectId { get; init; }

    /// <inheritdoc />
    public NativeDefinition? NativeDefinition { get; init; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <inheritdoc />
    public IReadOnlyList<InterchangeIssue> Notes { get; init; } = [];

    /// <summary>Gets or sets the schema reference.</summary>
    public ObjectRef SchemaRef { get; init; }

    /// <summary>Gets or sets the table kind.</summary>
    public ETableKind TableKind { get; init; } = ETableKind.Table;

    /// <summary>
    /// Gets or sets the table this one is a partition of, or an <c>INHERITS</c> child of; null for
    /// a table that stands on its own. The bound says which of the two it is.
    /// </summary>
    public ObjectRef? ParentRef { get; init; }

    /// <summary>
    /// Gets or sets the partition strategy of a partitioned table (<c>RANGE</c>, <c>LIST</c> or
    /// <c>HASH</c>), as the source declared it. Set on a partitioned table whether or not it is
    /// itself a partition, because a partition can be partitioned again.
    /// </summary>
    public string? PartitionStrategy { get; init; }

    /// <summary>
    /// Gets or sets the verbatim <c>FOR VALUES …</c> clause that binds a partition to its parent.
    /// Null on an <c>INHERITS</c> child, which has a parent but no bound, and on a plain table.
    /// </summary>
    public string? PartitionBound { get; init; }

    /// <summary>Gets or sets the columns.</summary>
    public IReadOnlyList<ColumnDefinition> Columns { get; init; } = [];

    /// <summary>Gets or sets the primary key.</summary>
    public PrimaryKeyDefinition? PrimaryKey { get; init; }

    /// <summary>Gets or sets the foreign keys.</summary>
    public IReadOnlyList<ForeignKeyDefinition> ForeignKeys { get; init; } = [];

    /// <summary>Gets or sets the unique constraints.</summary>
    public IReadOnlyList<UniqueConstraintDefinition> UniqueConstraints { get; init; } = [];

    /// <summary>Gets or sets the check constraints.</summary>
    public IReadOnlyList<CheckConstraintDefinition> CheckConstraints { get; init; } = [];

    /// <summary>Gets or sets the exclusion constraints.</summary>
    public IReadOnlyList<ExclusionConstraintDefinition> ExclusionConstraints { get; init; } = [];

    /// <summary>Gets or sets the indexes.</summary>
    public IReadOnlyList<IndexDefinition> Indexes { get; init; } = [];

    /// <summary>Gets or sets the triggers.</summary>
    public IReadOnlyList<TriggerDefinition> Triggers { get; init; } = [];

    /// <summary>Gets or sets the row order mode for export.</summary>
    public ERowOrder RowOrder { get; init; } = ERowOrder.PrimaryKey;
}

/// <summary>
/// A primary key constraint.
/// </summary>
public sealed record PrimaryKeyDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.PrimaryKey;

    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public string? SourceObjectId { get; init; }

    /// <inheritdoc />
    public NativeDefinition? NativeDefinition { get; init; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <inheritdoc />
    public IReadOnlyList<InterchangeIssue> Notes { get; init; } = [];

    /// <summary>Gets or sets the parent table reference.</summary>
    public ObjectRef TableRef { get; init; }

    /// <summary>Gets or sets the column names in order.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];
}

/// <summary>
/// A foreign key constraint.
/// </summary>
public sealed record ForeignKeyDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.ForeignKey;

    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public string? SourceObjectId { get; init; }

    /// <inheritdoc />
    public NativeDefinition? NativeDefinition { get; init; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <inheritdoc />
    public IReadOnlyList<InterchangeIssue> Notes { get; init; } = [];

    /// <summary>Gets or sets the parent table reference.</summary>
    public ObjectRef TableRef { get; init; }

    /// <summary>Gets or sets the referencing column names.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Gets or sets the referenced table reference.</summary>
    public ObjectRef ReferencedTableRef { get; init; }

    /// <summary>Gets or sets the referenced column names.</summary>
    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    /// <summary>Gets or sets the ON DELETE action.</summary>
    public string? OnDelete { get; init; }

    /// <summary>Gets or sets the ON UPDATE action.</summary>
    public string? OnUpdate { get; init; }

    /// <summary>Gets or sets whether the constraint is deferrable.</summary>
    public bool IsDeferrable { get; init; }

    /// <summary>Gets or sets whether the constraint is initially deferred.</summary>
    public bool IsInitiallyDeferred { get; init; }
}

/// <summary>
/// A unique constraint.
/// </summary>
public sealed record UniqueConstraintDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.UniqueConstraint;

    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public string? SourceObjectId { get; init; }

    /// <inheritdoc />
    public NativeDefinition? NativeDefinition { get; init; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <inheritdoc />
    public IReadOnlyList<InterchangeIssue> Notes { get; init; } = [];

    /// <summary>Gets or sets the parent table reference.</summary>
    public ObjectRef TableRef { get; init; }

    /// <summary>Gets or sets the column names in order.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];
}

/// <summary>
/// A check constraint.
/// </summary>
public sealed record CheckConstraintDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.CheckConstraint;

    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public string? SourceObjectId { get; init; }

    /// <inheritdoc />
    public NativeDefinition? NativeDefinition { get; init; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <inheritdoc />
    public IReadOnlyList<InterchangeIssue> Notes { get; init; } = [];

    /// <summary>Gets or sets the parent table reference.</summary>
    public ObjectRef TableRef { get; init; }

    /// <summary>Gets or sets the check expression.</summary>
    public DatabaseExpression Expression { get; init; } = new(string.Empty, string.Empty);
}

/// <summary>
/// An exclusion constraint.
/// </summary>
public sealed record ExclusionConstraintDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.ExclusionConstraint;

    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public string? SourceObjectId { get; init; }

    /// <inheritdoc />
    public NativeDefinition? NativeDefinition { get; init; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <inheritdoc />
    public IReadOnlyList<InterchangeIssue> Notes { get; init; } = [];

    /// <summary>Gets or sets the parent table reference.</summary>
    public ObjectRef TableRef { get; init; }
}

/// <summary>
/// An index.
/// </summary>
public sealed record IndexDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.Index;

    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public string? SourceObjectId { get; init; }

    /// <inheritdoc />
    public NativeDefinition? NativeDefinition { get; init; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <inheritdoc />
    public IReadOnlyList<InterchangeIssue> Notes { get; init; } = [];

    /// <summary>Gets or sets the parent table reference.</summary>
    public ObjectRef TableRef { get; init; }

    /// <summary>Gets or sets whether the index is unique.</summary>
    public bool IsUnique { get; init; }
}
