using System.Text.Json;

namespace CanonicalModel.Interchange;

/// <summary>
/// Origin of a routine implementation.
/// </summary>
public enum EOrigin
{
    /// <summary>Captured from the source database.</summary>
    Captured,

    /// <summary>Authored by a human for a specific dialect.</summary>
    Authored,

    /// <summary>Translated by a tool from another implementation.</summary>
    Translated,
}

/// <summary>
/// Requirements for deploying a routine implementation.
/// </summary>
public sealed record Requirements
{
    /// <summary>Gets or sets the required engine version range.</summary>
    public string? EngineVersionRange { get; init; }

    /// <summary>Gets or sets the required extensions.</summary>
    public IReadOnlyList<string> RequiredExtensions { get; init; } = [];

    /// <summary>Gets or sets the required procedural language.</summary>
    public string? RequiredLanguage { get; init; }

    /// <summary>Gets or sets additional notes.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Translation metadata for a translated implementation.
/// </summary>
public sealed record TranslatedBy
{
    /// <summary>Gets or sets the translator identifier.</summary>
    public string Translator { get; init; } = string.Empty;

    /// <summary>Gets or sets the source implementation reference.</summary>
    public ObjectRef SourceImplementationRef { get; init; }

    /// <summary>Gets or sets the mapping profile used.</summary>
    public string? MappingProfile { get; init; }

    /// <summary>Gets or sets the translation timestamp.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// A routine implementation (body) for a specific dialect.
/// </summary>
public sealed record RoutineImplementation
{
    /// <summary>Gets or sets the dialect this implementation is for.</summary>
    public string Dialect { get; init; } = string.Empty;

    /// <summary>Gets or sets the procedural language.</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>Gets or sets the routine body text.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Gets or sets the deployment requirements.</summary>
    public Requirements? Requirements { get; init; }

    /// <summary>Gets or sets the origin of this implementation.</summary>
    public EOrigin Origin { get; init; } = EOrigin.Captured;

    /// <summary>Gets or sets the translation metadata (if Origin is Translated).</summary>
    public TranslatedBy? TranslatedBy { get; init; }
}

/// <summary>
/// A routine (function, procedure, aggregate, or window function) in the interchange model.
/// </summary>
public sealed record RoutineDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind { get; init; } = EObjectKind.Function;

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

    /// <summary>Gets or sets the implementations (one per dialect).</summary>
    public IReadOnlyList<RoutineImplementation> Implementations { get; init; } = [];

    /// <summary>Gets or sets the return type.</summary>
    public string? ReturnType { get; init; }

    /// <summary>Gets or sets the parameters.</summary>
    public IReadOnlyList<RoutineParameter> Parameters { get; init; } = [];
}

/// <summary>
/// A routine parameter.
/// </summary>
public sealed record RoutineParameter
{
    /// <summary>Gets or sets the parameter name.</summary>
    public string? Name { get; init; }

    /// <summary>Gets or sets the parameter data type.</summary>
    public string DataType { get; init; } = string.Empty;

    /// <summary>Gets or sets the parameter mode (IN, OUT, INOUT, VARIADIC).</summary>
    public string Mode { get; init; } = "IN";

    /// <summary>Gets or sets the default value expression.</summary>
    public DatabaseExpression? DefaultValue { get; init; }
}

/// <summary>
/// Trigger timing.
/// </summary>
public enum ETriggerTiming
{
    /// <summary>BEFORE the triggering event.</summary>
    Before,

    /// <summary>AFTER the triggering event.</summary>
    After,

    /// <summary>INSTEAD OF the triggering event (for views).</summary>
    InsteadOf,
}

/// <summary>
/// Trigger event.
/// </summary>
[Flags]
public enum ETriggerEvent
{
    /// <summary>INSERT event.</summary>
    Insert = 1,

    /// <summary>UPDATE event.</summary>
    Update = 2,

    /// <summary>DELETE event.</summary>
    Delete = 4,

    /// <summary>TRUNCATE event.</summary>
    Truncate = 8,
}

/// <summary>
/// Trigger orientation.
/// </summary>
public enum ETriggerOrientation
{
    /// <summary>FOR EACH ROW.</summary>
    Row,

    /// <summary>FOR EACH STATEMENT.</summary>
    Statement,
}

/// <summary>
/// A trigger in the interchange model.
/// </summary>
public sealed record TriggerDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.Trigger;

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

    /// <summary>Gets or sets the trigger timing.</summary>
    public ETriggerTiming Timing { get; init; }

    /// <summary>Gets or sets the trigger events.</summary>
    public ETriggerEvent Events { get; init; }

    /// <summary>Gets or sets the trigger orientation.</summary>
    public ETriggerOrientation Orientation { get; init; } = ETriggerOrientation.Row;

    /// <summary>Gets or sets the enabled state.</summary>
    public string EnabledState { get; init; } = "O";

    /// <summary>Gets or sets the referenced routine reference.</summary>
    public ObjectRef ReferencedRoutineRef { get; init; }

    /// <summary>Gets or sets the trigger arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Gets or sets the WHEN condition.</summary>
    public DatabaseExpression? When { get; init; }
}
