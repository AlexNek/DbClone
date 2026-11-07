using System.Text.Json;

using CanonicalModel.Interchange;

namespace DbAdapter.Definitions.Interchange;

/// <summary>
/// A database object that has no structured home in the neutral interchange model, so it travels as
/// the statement text its source engine's dialect speaks.
/// <para>
/// These objects are <see cref="ISchemaObject"/>s, which lets a restore stage be handed them beside
/// the structured ones: the kind is the neutral <see cref="EObjectKind"/>, the identity is computed by
/// the same graph every other object's identity comes from, and the SQL lives in
/// <see cref="NativeDefinition"/> carrying its dialect and whether it is a whole statement — the same
/// two facts the structured sections record, and decided against at import rather than at export,
/// because at export the restore target is not known yet.
/// </para>
/// <para>
/// <see cref="SchemaName"/> and <see cref="Dependencies"/> stay names rather than ids. The statement
/// in <see cref="NativeDefinition"/> already carries the qualification the source reported the object
/// with, so nothing is resolved to build it, and what <see cref="Dependencies"/> names may be no
/// object of this catalog at all — a publication lists tables of the database, a subscription names a
/// publication of another one.
/// </para>
/// </summary>
public sealed record NativeObjectDefinition : ISchemaObject
{
    /// <inheritdoc />
    public InterchangeId Id { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind { get; init; }

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

    /// <summary>Gets the schema that contains the object; empty for an object the database keeps
    /// globally, such as a publication.</summary>
    public string SchemaName { get; init; } = string.Empty;

    /// <summary>Gets the names of the objects this one depends on, as the source reported them.</summary>
    public IReadOnlyList<string>? Dependencies { get; init; }
}
