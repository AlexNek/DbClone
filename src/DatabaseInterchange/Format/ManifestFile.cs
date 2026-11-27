using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabaseInterchange.Format;

/// <summary>
/// The manifest.json structure.
/// </summary>
public sealed record ManifestFile
{
    /// <summary>Gets or sets the format version.</summary>
    public int FormatVersion { get; init; } = InterchangeFormat.Version;

    /// <summary>Gets or sets the minimum reader version.</summary>
    public int MinimumReader { get; init; } = InterchangeFormat.MinimumReader;

    /// <summary>Gets or sets the producer identifier.</summary>
    public string Producer { get; init; } = string.Empty;

    /// <summary>Gets or sets whether the catalog is complete.</summary>
    public bool Complete { get; init; } = true;

    /// <summary>
    /// Gets or sets the model format: "direct" (DatabaseModel as-is) or "interchange" (neutral types).
    /// Direct format is used for same-type transfers; interchange for cross-database transfers.
    /// </summary>
    public string ModelFormat { get; init; } = "direct";

    /// <summary>
    /// Gets or sets the source database dialect (e.g., "postgresql", "mysql").
    /// Recorded at export time; used at import to determine if conversion is needed.
    /// </summary>
    public string SourceDialect { get; init; } = string.Empty;

    /// <summary>Gets or sets the sections present in the catalog.</summary>
    public IReadOnlyList<string> Sections { get; init; } = [];

    /// <summary>Gets or sets the object counts.</summary>
    public ManifestCounts Counts { get; init; } = new();

    /// <summary>Gets or sets the data encoding information.</summary>
    public ManifestData Data { get; init; } = new();

    /// <summary>Gets or sets the file entries with hashes.</summary>
    public IReadOnlyList<ManifestFileEntry> Files { get; init; } = [];
}

/// <summary>
/// Object counts in the manifest.
/// </summary>
public sealed record ManifestCounts
{
    /// <summary>Gets or sets the number of schemas.</summary>
    public int Schemas { get; init; }

    /// <summary>Gets or sets the number of tables.</summary>
    public int Tables { get; init; }

    /// <summary>Gets or sets the number of columns.</summary>
    public int Columns { get; init; }

    /// <summary>Gets or sets the total number of rows.</summary>
    public long Rows { get; init; }

    /// <summary>Gets or sets the total number of chunks.</summary>
    public int Chunks { get; init; }
}

/// <summary>
/// Data encoding information in the manifest.
/// </summary>
public sealed record ManifestData
{
    /// <summary>Gets or sets the row encoding format.</summary>
    public string Encoding { get; init; } = "csv";

    /// <summary>Gets or sets the default chunk size in bytes.</summary>
    public int DefaultChunkBytes { get; init; } = 32 * 1024 * 1024;
}

/// <summary>
/// A file entry in the manifest.
/// </summary>
public sealed record ManifestFileEntry
{
    /// <summary>Gets or sets the file path relative to the catalog root.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long Bytes { get; init; }

    /// <summary>Gets or sets the SHA-256 hash of the file content.</summary>
    public string Sha256 { get; init; } = string.Empty;
}
