namespace CanonicalModel.Interchange;

/// <summary>
/// The base interface for all canonical schema objects in the interchange model.
/// Every object has a content-derived identity and a kind.
/// </summary>
public interface ISchemaObject
{
    /// <summary>Gets the content-derived identity (SHA-256 hash).</summary>
    InterchangeId Id { get; }

    /// <summary>Gets the object kind.</summary>
    EObjectKind Kind { get; }

    /// <summary>Gets the object name (without schema qualification).</summary>
    string Name { get; }

    /// <summary>Gets the optional source-reported object identifier (e.g., PostgreSQL OID).</summary>
    string? SourceObjectId { get; }

    /// <summary>Gets the optional native SQL definition.</summary>
    NativeDefinition? NativeDefinition { get; }

    /// <summary>Gets the extension data (namespaced key-value pairs).</summary>
    IReadOnlyDictionary<string, System.Text.Json.JsonElement> ExtensionData { get; }

    /// <summary>Gets the notes (issues, omissions, observations).</summary>
    IReadOnlyList<InterchangeIssue> Notes { get; }
}
