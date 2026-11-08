using System.Text.Json;

namespace CanonicalModel.Interchange;

/// <summary>
/// A schema in the interchange model.
/// </summary>
public sealed record SchemaDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.Schema;

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

    /// <summary>Gets or sets the owner role name.</summary>
    public string? Owner { get; init; }
}

/// <summary>
/// A sequence in the interchange model.
/// </summary>
public sealed record SequenceDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.Sequence;

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

    /// <summary>Gets or sets the data type.</summary>
    public string DataType { get; init; } = "bigint";

    /// <summary>Gets or sets the start value.</summary>
    public long StartValue { get; init; } = 1;

    /// <summary>Gets or sets the increment value.</summary>
    public long Increment { get; init; } = 1;

    /// <summary>Gets or sets the minimum value.</summary>
    public long? MinValue { get; init; }

    /// <summary>Gets or sets the maximum value.</summary>
    public long? MaxValue { get; init; }

    /// <summary>Gets or sets the cache value.</summary>
    public long Cache { get; init; } = 1;

    /// <summary>Gets or sets whether the sequence cycles.</summary>
    public bool Cycle { get; init; }

    /// <summary>Gets or sets the current value (captured state).</summary>
    public long? CurrentValue { get; init; }

    /// <summary>Gets or sets whether the sequence has been called (is_called).</summary>
    public bool? IsCalled { get; init; }
}

/// <summary>
/// A view in the interchange model.
/// </summary>
public sealed record ViewDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.View;

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

    /// <summary>Gets or sets the view definition SQL.</summary>
    public DatabaseExpression? Definition { get; init; }

    /// <summary>Gets or sets the columns.</summary>
    public IReadOnlyList<ColumnDefinition> Columns { get; init; } = [];

    /// <summary>Gets or sets whether this is a materialized view.</summary>
    public bool IsMaterialized { get; init; }
}
