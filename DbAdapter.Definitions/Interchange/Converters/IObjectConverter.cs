using DbAdapter.Definitions.Interchange;

namespace DbAdapter.Definitions.Interchange.Converters;

/// <summary>
/// Converts native database objects from one dialect to another.
/// Implementations handle specific source→target dialect pairs.
/// </summary>
/// <remarks>
/// <para>
/// Converters are plugins that enable cross-dialect transfer of otherwise non-portable objects
/// (functions, triggers, views, etc.). Each converter handles one or more specific dialect pairs.
/// </para>
/// <para>
/// A converter may not be able to convert all objects - some constructs may be too dialect-specific
/// to translate automatically. In such cases, the converter should return <c>null</c> or throw
/// <see cref="ConversionFailedException"/> with a clear explanation.
/// </para>
/// </remarks>
public interface IObjectConverter
{
    /// <summary>
    /// Gets the source dialect this converter handles (e.g., "postgresql").
    /// </summary>
    string SourceDialect { get; }

    /// <summary>
    /// Gets the target dialect this converter produces (e.g., "mysql").
    /// </summary>
    string TargetDialect { get; }

    /// <summary>
    /// Gets the object kinds this converter can handle (e.g., "function", "trigger", "view").
    /// </summary>
    IReadOnlySet<string> SupportedObjectKinds { get; }

    /// <summary>
    /// Determines if this converter can handle the specified object.
    /// </summary>
    /// <param name="objectKind">The kind of object (function, trigger, view, etc.).</param>
    /// <returns>True if this converter supports the object kind; false otherwise.</returns>
    bool CanConvert(string objectKind);

    /// <summary>
    /// Converts a native object from the source dialect to the target dialect.
    /// </summary>
    /// <param name="source">The native object in source dialect format.</param>
    /// <param name="context">Conversion context with target database information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The converted native object in target dialect format, or <c>null</c> if conversion
    /// is not possible (the object will be skipped with a warning).
    /// </returns>
    /// <exception cref="ConversionFailedException">
    /// Thrown when conversion fails but should have succeeded. The exception message explains
    /// what went wrong and may suggest manual intervention.
    /// </exception>
    Task<NativeObjectDefinition?> ConvertAsync(
        NativeObjectDefinition source,
        ConversionContext context,
        CancellationToken ct = default);
}
