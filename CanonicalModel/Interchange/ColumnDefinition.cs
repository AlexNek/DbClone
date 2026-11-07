using System.Text.Json;

namespace CanonicalModel.Interchange;

/// <summary>
/// A column in the interchange model.
/// </summary>
public sealed record ColumnDefinition : ISchemaObject
{
    /// <inheritdoc />
    public InterchangeId Id { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.Column;

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

    /// <summary>Gets or sets the parent table's Id.</summary>
    public InterchangeId TableId { get; init; }

    /// <summary>Gets or sets the ordinal position (1-based).</summary>
    public int Ordinal { get; init; }

    /// <summary>Gets or sets the native type name.</summary>
    public NativeType NativeType { get; init; } = new(string.Empty);

    /// <summary>Gets or sets the logical type.</summary>
    public ELogicalType LogicalType { get; init; }

    /// <summary>Gets or sets whether the column is nullable.</summary>
    public bool Nullable { get; init; }

    /// <summary>Gets or sets the declared length (for varchar, etc.).</summary>
    public int? Length { get; init; }

    /// <summary>Gets or sets the precision (for numeric, etc.).</summary>
    public int? Precision { get; init; }

    /// <summary>Gets or sets the scale (for numeric, etc.).</summary>
    public int? Scale { get; init; }

    /// <summary>Gets or sets the default value expression.</summary>
    public DatabaseExpression? DefaultValue { get; init; }

    /// <summary>Gets or sets the identity specification.</summary>
    public string? Identity { get; init; }

    /// <summary>Gets or sets the collation.</summary>
    public string? Collation { get; init; }

    /// <summary>Gets or sets the generation expression.</summary>
    public DatabaseExpression? Generated { get; init; }

    /// <summary>Gets or sets the description/comment.</summary>
    public string? Description { get; init; }
}
