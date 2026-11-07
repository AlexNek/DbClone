namespace DbAdapter.Definitions.Interchange;

/// <summary>
/// Represents a database object in its native dialect-specific format.
/// Used for objects that cannot be converted between dialects (functions, triggers, views, etc.).
/// </summary>
/// <param name="ObjectKind">The type of object (function, trigger, view, etc.).</param>
/// <param name="SchemaName">The schema containing the object.</param>
/// <param name="Name">The object name.</param>
/// <param name="NativeSql">The native SQL definition text (dialect-specific).</param>
/// <param name="SourceDialect">The dialect this definition came from (e.g., "postgresql").</param>
/// <param name="Dependencies">Optional list of schema-qualified names this object depends on.</param>
public sealed record NativeObjectDefinition(
    string ObjectKind,
    string SchemaName,
    string Name,
    string NativeSql,
    string SourceDialect,
    IReadOnlyList<string>? Dependencies = null);
