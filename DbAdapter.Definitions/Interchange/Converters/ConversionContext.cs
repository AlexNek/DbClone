namespace DbAdapter.Definitions.Interchange.Converters;

/// <summary>
/// Context information for object conversion.
/// </summary>
/// <param name="TargetDialect">The target database dialect.</param>
/// <param name="TargetVersion">The target database version (if known).</param>
/// <param name="TargetSchema">The target schema name (if different from source).</param>
/// <param name="Options">Additional conversion options.</param>
public sealed record ConversionContext(
    string TargetDialect,
    string? TargetVersion = null,
    string? TargetSchema = null,
    ConversionOptions? Options = null);

/// <summary>
/// Options that control conversion behavior.
/// </summary>
public sealed record ConversionOptions
{
    /// <summary>
    /// Gets or sets whether to preserve comments from the source definition.
    /// </summary>
    public bool PreserveComments { get; init; } = true;

    /// <summary>
    /// Gets or sets whether to convert data type names (e.g., SERIAL → INT AUTO_INCREMENT).
    /// </summary>
    public bool ConvertDataTypes { get; init; } = true;

    /// <summary>
    /// Gets or sets whether to attempt conversion of dialect-specific functions.
    /// </summary>
    public bool ConvertFunctions { get; init; } = true;

    /// <summary>
    /// Gets or sets custom type mappings (source type → target type).
    /// </summary>
    public IReadOnlyDictionary<string, string>? TypeMappings { get; init; }

    /// <summary>
    /// Gets or sets whether conversion should fail on unsupported constructs
    /// or skip them with warnings.
    /// </summary>
    public bool FailOnUnsupported { get; init; } = false;
}
