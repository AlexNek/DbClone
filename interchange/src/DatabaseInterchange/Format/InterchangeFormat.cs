namespace DatabaseInterchange.Format;

/// <summary>
/// Constants for the interchange format.
/// </summary>
public static class InterchangeFormat
{
    /// <summary>The current format version.</summary>
    /// <remarks>
    /// <para>Version 4: A sequence records whether an identity column created it, so a restore leaves such a sequence to its column instead of creating a second one beside it.</para>
    /// <para>Version 3: Every non-NULL binary value is stored in its own blob file and the CSV carries only a reference.</para>
    /// <para>Version 1: Initial format with streaming row files and chunk-based storage.</para>
    /// </remarks>
    public const int Version = 4;

    /// <summary>The minimum reader version that can read this format.</summary>
    /// <remarks>
    /// Version 4 requires a reader that understands that a catalog states which of its sequences an
    /// identity column created — a version 3 reader would create those sequences itself and leave an
    /// unused copy beside each identity.
    /// </remarks>
    public const int MinimumReader = 4;

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
