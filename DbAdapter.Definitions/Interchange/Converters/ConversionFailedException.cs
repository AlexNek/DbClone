namespace DbAdapter.Definitions.Interchange.Converters;

/// <summary>
/// Exception thrown when object conversion fails.
/// </summary>
public class ConversionFailedException : Exception
{
    /// <summary>
    /// Gets the object kind that faileded to convert.
    /// </summary>
    public string ObjectKind { get; }

    /// <summary>
    /// Gets the source dialect.
    /// </summary>
    public string SourceDialect { get; }

    /// <summary>
    /// Gets the target dialect.
    /// </summary>
    public string TargetDialect { get; }

    /// <summary>
    /// Gets the object name (schema.name format).
    /// </summary>
    public string ObjectName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversionFailedException"/> class.
    /// </summary>
    public ConversionFailedException(
        string message,
        string objectKind,
        string sourceDialect,
        string targetDialect,
        string objectName)
        : base(message)
    {
        ObjectKind = objectKind;
        SourceDialect = sourceDialect;
        TargetDialect = targetDialect;
        ObjectName = objectName;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversionFailedException"/> class.
    /// </summary>
    public ConversionFailedException(
        string message,
        string objectKind,
        string sourceDialect,
        string targetDialect,
        string objectName,
        Exception innerException)
        : base(message, innerException)
    {
        ObjectKind = objectKind;
        SourceDialect = sourceDialect;
        TargetDialect = targetDialect;
        ObjectName = objectName;
    }
}
