using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CanonicalModel.Interchange;
using DbAdapter.Definitions;

namespace DatabaseInterchange.Format;

/// <summary>
/// Reads a catalog directory.
/// </summary>
public sealed class CatalogReader : IAsyncDisposable
{
    private readonly string _catalogPath;
    private ManifestFile? _manifest;
    private CatalogIndex? _catalogIndex;
    private bool _disposed;

    private CatalogReader(string catalogPath)
    {
        _catalogPath = catalogPath;
    }

    /// <summary>
    /// Gets the manifest for this catalog.
    /// </summary>
    public ManifestFile Manifest => _manifest ?? throw new InvalidOperationException("Catalog not opened.");

    /// <summary>
    /// Gets the catalog index.
    /// </summary>
    public CatalogIndex Index => _catalogIndex ?? throw new InvalidOperationException("Catalog not opened.");

    /// <summary>
    /// Opens a catalog directory for reading.
    /// </summary>
    /// <param name="catalogPath">The catalog directory path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A catalog reader.</returns>
    public static async Task<CatalogReader> OpenAsync(string catalogPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);

        if (!Directory.Exists(catalogPath))
        {
            throw new DirectoryNotFoundException($"Catalog directory not found: {catalogPath}");
        }

        var reader = new CatalogReader(catalogPath);
        await reader.LoadManifestAsync(ct);
        await reader.LoadCatalogIndexAsync(ct);

        return reader;
    }

    private async Task LoadManifestAsync(CancellationToken ct)
    {
        var manifestPath = Path.Combine(_catalogPath, InterchangeFormat.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Manifest file not found: {manifestPath}");
        }

        var json = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, ct);
        _manifest = JsonSerializer.Deserialize<ManifestFile>(json, CatalogJson.ReaderOptions)
            ?? throw new InvalidOperationException("Failed to deserialize manifest.");

        // Version gate
        if (_manifest.FormatVersion > InterchangeFormat.Version)
        {
            throw new InvalidOperationException(
                $"Catalog format version {_manifest.FormatVersion} is newer than supported version {InterchangeFormat.Version}.");
        }

        if (_manifest.MinimumReader > InterchangeFormat.MinimumReader)
        {
            throw new InvalidOperationException(
                $"Catalog requires minimum reader version {_manifest.MinimumReader}, but this reader is version {InterchangeFormat.MinimumReader}.");
        }
    }

    private async Task LoadCatalogIndexAsync(CancellationToken ct)
    {
        var catalogPath = Path.Combine(_catalogPath, InterchangeFormat.CatalogFileName);
        if (!File.Exists(catalogPath))
        {
            throw new InvalidOperationException($"Catalog index file not found: {catalogPath}");
        }

        var json = await File.ReadAllTextAsync(catalogPath, Encoding.UTF8, ct);
        _catalogIndex = JsonSerializer.Deserialize<CatalogIndex>(json, CatalogJson.ReaderOptions)
            ?? throw new InvalidOperationException("Failed to deserialize catalog index.");
    }

    /// <summary>
    /// Reads a schema section from the catalog.
    /// </summary>
    /// <typeparam name="T">The type of objects in the section.</typeparam>
    /// <param name="sectionName">The section name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The objects in the section.</returns>
    public async Task<IReadOnlyList<T>> ReadSectionAsync<T>(string sectionName, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        var filePath = Path.Combine(_catalogPath, InterchangeFormat.SchemaDirectory, $"{sectionName}.json");
        if (!File.Exists(filePath))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct);
        var section = JsonSerializer.Deserialize<SectionFile<T>>(json, CatalogJson.ReaderOptions);
        return section?.Objects ?? [];
    }

    /// <summary>
    /// Reads the security section from the catalog.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The captured security section, or <c>null</c> when the catalog carries no security file.
    /// The two states are different and neither may be collapsed into the other: <c>null</c> means
    /// the producer captured no security at all, while a section holding empty lists means it
    /// captured the kinds it could and found nothing of a kind it did look for.
    /// </returns>
    public async Task<SecuritySection?> ReadSecurityAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var filePath = Path.Combine(
            _catalogPath,
            InterchangeFormat.SchemaDirectory,
            "security.json");
        if (!File.Exists(filePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct);
        var section = JsonSerializer.Deserialize<SecuritySectionFile>(json, CatalogJson.ReaderOptions);
        return section?.Object;
    }

    /// <summary>
    /// Reads a <see cref="CanonicalModel.Models.DatabaseModel"/> directly from the catalog.
    /// Used for same-type transfers where no conversion is needed.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The database model, or null if not in direct format.</returns>
    public async Task<CanonicalModel.Models.DatabaseModel?> ReadDatabaseModelAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Check if this catalog is in direct format
        if (!string.Equals(_manifest?.ModelFormat, "direct", StringComparison.OrdinalIgnoreCase))
        {
            return null; // Not a direct-format catalog
        }

        var filePath = Path.Combine(_catalogPath, InterchangeFormat.SchemaDirectory, "model.json");
        if (!File.Exists(filePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct);
        var serializer = new DirectCatalogSerializer();
        return serializer.Deserialize(json);
    }

    /// <summary>
    /// Gets whether this catalog is in direct format (same-type transfer).
    /// </summary>
    public bool IsDirectFormat =>
        string.Equals(_manifest?.ModelFormat, "direct", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the source dialect recorded at export time.
    /// </summary>
    public string SourceDialect => _manifest?.SourceDialect ?? string.Empty;

    /// <summary>
    /// Reads rows from a table's chunks.
    /// </summary>
    /// <param name="tableId">The table's interchange Id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of rows.</returns>
    public async IAsyncEnumerable<DataValue[]> ReadRowsAsync(
        string tableId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableId);

        var tableDir = Path.Combine(_catalogPath, InterchangeFormat.DataDirectory, tableId);
        if (!Directory.Exists(tableDir))
        {
            yield break;
        }

        // Read table.json for chunk list
        var tableManifestPath = Path.Combine(tableDir, "table.json");
        if (!File.Exists(tableManifestPath))
        {
            yield break;
        }

        var tableJson = await File.ReadAllTextAsync(tableManifestPath, Encoding.UTF8, ct);
        var tableManifest = JsonSerializer.Deserialize<TableManifest>(tableJson, CatalogJson.ReaderOptions);
        if (tableManifest is null)
        {
            yield break;
        }

        // Read each chunk in order
        foreach (var chunk in tableManifest.Chunks)
        {
            ct.ThrowIfCancellationRequested();

            var chunkPath = Path.Combine(tableDir, chunk.File);
            if (!File.Exists(chunkPath))
            {
                continue;
            }

            await using var stream = File.OpenRead(chunkPath);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var values = JsonSerializer.Deserialize<string?[]>(line, CatalogJson.ReaderOptions);
                if (values is not null)
                {
                    yield return values.Select((v, i) =>
                    {
                        var logicalType = i < tableManifest.Columns.Count
                            ? Enum.Parse<ELogicalType>(tableManifest.Columns[i].LogicalType, ignoreCase: true)
                            : ELogicalType.Unknown;
                        return v is null ? DataValue.Null(logicalType) : DataValue.FromText(logicalType, v);
                    }).ToArray();
                }
            }
        }
    }

    /// <summary>
    /// Reads the row count the catalog recorded for a table.
    /// </summary>
    /// <param name="tableId">The table's interchange Id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The count the table's manifest states, or <see langword="null"/> when the catalog holds
    /// no data folder for the table — which is what an export run without data writes.</returns>
    public async Task<long?> ReadTableRowCountAsync(string tableId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableId);

        var tableManifestPath = Path.Combine(
            _catalogPath,
            InterchangeFormat.DataDirectory,
            tableId,
            "table.json");
        if (!File.Exists(tableManifestPath))
        {
            return null;
        }

        var tableJson = await File.ReadAllTextAsync(tableManifestPath, Encoding.UTF8, ct);
        var tableManifest = JsonSerializer.Deserialize<TableManifest>(tableJson, CatalogJson.ReaderOptions);

        return tableManifest?.RowCount;
    }

    /// <summary>
    /// Verifies the integrity of the catalog by checking file hashes.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A verification result.</returns>
    public async Task<CatalogVerificationResult> VerifyAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var issues = new List<string>();

        foreach (var fileEntry in Manifest.Files)
        {
            ct.ThrowIfCancellationRequested();

            var filePath = Path.Combine(_catalogPath, fileEntry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(filePath))
            {
                issues.Add($"Missing file: {fileEntry.Path}");
                continue;
            }

            var actualHash = await ComputeSha256Async(filePath, ct);
            if (!string.Equals(actualHash, fileEntry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"Hash mismatch for {fileEntry.Path}: expected {fileEntry.Sha256}, got {actualHash}");
            }
        }

        await Task.CompletedTask;

        return new CatalogVerificationResult
        {
            IsValid = issues.Count == 0,
            Issues = issues,
        };
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}
