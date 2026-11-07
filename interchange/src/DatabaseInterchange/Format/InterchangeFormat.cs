namespace DatabaseInterchange.Format;

/// <summary>
/// Constants for the interchange format.
/// </summary>
public static class InterchangeFormat
{
    /// <summary>The current format version.</summary>
    public const int Version = 1;

    /// <summary>The minimum reader version that can read this format.</summary>
    public const int MinimumReader = 1;

    /// <summary>The directory suffix for catalog archives.</summary>
    public const string DirectorySuffix = ".rdbx";

    /// <summary>The manifest file name.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>The catalog index file name.</summary>
    public const string CatalogFileName = "catalog.json";

    /// <summary>The export metadata file name.</summary>
    public const string ExportFileName = "export.json";

    /// <summary>The schema directory name.</summary>
    public const string SchemaDirectory = "schema";

    /// <summary>The data directory name.</summary>
    public const string DataDirectory = "data";

    /// <summary>The native objects directory name (dialect-specific SQL definitions).</summary>
    public const string NativeDirectory = "native";

    /// <summary>The unknown directory name.</summary>
    public const string UnknownDirectory = "unknown";
}
