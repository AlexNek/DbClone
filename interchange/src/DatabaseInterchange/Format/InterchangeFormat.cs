namespace DatabaseInterchange.Format;

/// <summary>
/// Constants for the interchange format.
/// </summary>
public static class InterchangeFormat
{
    /// <summary>The current format version.</summary>
    /// <remarks>
    /// <para>Version 3: Every non-NULL binary value is stored in its own blob file and the CSV carries only a reference.</para>
    /// <para>Version 1: Initial format with streaming row files and chunk-based storage.</para>
    /// </remarks>
    public const int Version = 3;

    /// <summary>The minimum reader version that can read this format.</summary>
    /// <remarks>Version 3 is the per-value blob-file format and requires a version 3 reader.</remarks>
    public const int MinimumReader = 3;

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
