using System.Security.Cryptography;
using CanonicalModel.Interchange;
using DbAdapter.Definitions;
using DatabaseInterchange.Script;

namespace DatabaseInterchange.Format;

/// <summary>
/// Reads a catalog directory.
/// </summary>
public sealed class CatalogReader : IAsyncDisposable
{
    private readonly string _catalogPath;
    private ManifestFile? _manifest;
    private CatalogIndex? _catalogIndex;
    private Dictionary<(string Schema, string Table), string>? _tableFolders;
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

        _manifest = await JsonFile.ReadDocumentAsync<ManifestFile>(manifestPath, ct)
            ?? throw new InvalidOperationException("Failed to deserialize manifest.");

        if (_manifest.FormatVersion < InterchangeFormat.Version)
        {
            throw new InvalidOperationException(
                $"This catalog was written by interchange format version {_manifest.FormatVersion}, which this version cannot read. Re-export the database.");
        }

        if (string.Equals(_manifest.Data.Encoding, "ndjson", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This catalog uses the legacy NDJSON row format, which this version cannot read. Re-export the database.");
        }

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

        _catalogIndex = await JsonFile.ReadDocumentAsync<CatalogIndex>(catalogPath, ct)
            ?? throw new InvalidOperationException("Failed to deserialize catalog index.");
    }

    /// <summary>
    /// Reads a schema section from the catalog using streaming JSON deserialization.
    /// </summary>
    /// <typeparam name="T">The type of objects in the section.</typeparam>
    /// <param name="sectionName">The section name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The objects in the section.</returns>
    /// <remarks>
    /// The section is enumerated one object at a time from the file's own stream, so a section holding a
    /// million columns is parsed a column at a time and the file's text is never held whole. The returned
    /// list is what the public contract promises; it is not how the file was read.
    /// </remarks>
    public async Task<IReadOnlyList<T>> ReadSectionAsync<T>(string sectionName, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        var filePath = Path.Combine(_catalogPath, InterchangeFormat.SchemaDirectory, $"{sectionName}.json");
        if (!File.Exists(filePath))
        {
            return [];
        }

        var objects = new List<T>();
        await foreach (var obj in JsonFile.ReadObjectsAsync<T>(filePath, "objects", ct))
        {
            objects.Add(obj);
        }

        return objects;
    }

    /// <summary>
    /// Reads the security section from the catalog using streaming JSON deserialization.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The captured security section, or <c>null</c> when the catalog carries no security file.
    /// The two states are different and neither may be collapsed into the other: <c>null</c> means
    /// the producer captured no security at all, while a section holding empty lists means it
    /// captured the kinds it could and found nothing of a kind it did look for.
    /// </returns>
    /// <remarks>
    /// Security is one compound object rather than an array, so the file is read as one document over its
    /// stream — the intermediate <see cref="string"/> the older text read produced is never built.
    /// </remarks>
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
    
        var section = await JsonFile.ReadDocumentAsync<SecuritySectionFile>(filePath, ct);
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

        await using var stream = await JsonFile.OpenReadAsync(filePath, ct);
        var serializer = new DirectCatalogSerializer();
        return await serializer.DeserializeAsync(stream, ct);
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
    /// <param name="schemaName">The table's schema name.</param>
    /// <param name="tableName">The table's name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of rows.</returns>
    /// <remarks>
    /// <para>Rows are streamed one record at a time through <see cref="CsvRowReader"/>; the reader never
    /// holds the whole chunk file in memory. Each decoded field goes through
    /// <see cref="BinaryFieldGrammar"/>, so a blob reference is validated and turned into a range payload at
    /// the moment it is read — the row that reaches the caller already carries bytes-or-a-pointer decided by
    /// the column's own logical type.</para>
    /// <para>Phase 4B: Chunk files are discovered via <c>checksums.sha256</c> rather than from a
    /// <c>chunks[]</c> array in <c>table.json</c>. This keeps the manifest bounded in size regardless
    /// of how many chunks the table has.</para>
    /// </remarks>
    public async IAsyncEnumerable<DataValue[]> ReadRowsAsync(
        string schemaName,
        string tableName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var tableDir = ResolveTableDirectory(schemaName, tableName);
        if (tableDir is null)
        {
            yield break;
        }

        // Read table.json for column metadata
        var tableManifestPath = Path.Combine(tableDir, "table.json");
        if (!File.Exists(tableManifestPath))
        {
            yield break;
        }

        var tableManifest = await JsonFile.ReadDocumentAsync<TableManifest>(tableManifestPath, ct);
        if (tableManifest is null)
        {
            yield break;
        }

        // The column types come from the manifest and are parsed once for the table: the grammar needs the
        // type of the column each field belongs to, and re-parsing the same name for every row of every
        // chunk is work the layout already answered.
        var columnTypes = new List<ELogicalType>(tableManifest.Columns.Count);
        foreach (var column in tableManifest.Columns)
        {
            columnTypes.Add(Enum.Parse<ELogicalType>(column.LogicalType, ignoreCase: true));
        }

        // Discover chunk files from checksums.sha256 (Phase 4B)
        var chunkFiles = await DiscoverChunkFilesAsync(tableDir, ct);

        // One grammar per table: it owns the container reader the references of this folder resolve against.
        await using var grammar = new BinaryFieldGrammar(tableDir);

        // Read each chunk in order through the streaming row reader
        foreach (var chunkFile in chunkFiles)
        {
            ct.ThrowIfCancellationRequested();

            var chunkPath = Path.Combine(tableDir, chunkFile);
            if (!File.Exists(chunkPath))
            {
                continue;
            }

            await using var rowReader = new CsvRowReader(
                chunkPath,
                grammar,
                columnTypes,
                tableManifest.Columns.Select(column => column.Name).ToList());

            DataValue[]? row;
            while ((row = await rowReader.ReadRecordAsync(ct)) is not null)
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// Discovers chunk files from the <c>checksums.sha256</c> file in the table directory.
    /// </summary>
    /// <remarks>
    /// Phase 4B: The checksums file is the authoritative per-file list. Chunk files are identified
    /// by their canonical <c>data.NNNNN.csv</c> name and returned in sorted order.
    /// </remarks>
    /// <param name="tableDir">The table's data directory.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Chunk file names in order.</returns>
    private static async Task<IReadOnlyList<string>> DiscoverChunkFilesAsync(string tableDir, CancellationToken ct)
    {
        var checksumsPath = Path.Combine(tableDir, "checksums.sha256");
        if (!File.Exists(checksumsPath))
        {
            // A v2 catalog is expected to have checksums, but keep the directory fallback for
            // incomplete catalogs so verification can report the missing file separately.
            return Directory.GetFiles(tableDir, "data.*.csv")
                .Select(Path.GetFileName)
                .Where(f => f is not null)
                .Select(f => f!)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        var chunkFiles = new List<string>();
        var lines = await File.ReadAllLinesAsync(checksumsPath, ct);
        foreach (var line in lines)
        {
            // Format: <relative-path>\t<sha256>
            var tabIdx = line.IndexOf('\t');
            if (tabIdx <= 0) continue;

            var relativePath = line[..tabIdx];
            // CSV chunk files are at the table folder root and use the canonical data.NNNNN.csv name.
            if (!relativePath.Contains('/')
                && relativePath.StartsWith("data.", StringComparison.Ordinal)
                && relativePath.EndsWith(".csv", StringComparison.Ordinal))
            {
                chunkFiles.Add(relativePath);
            }
        }

        chunkFiles.Sort(StringComparer.Ordinal);
        return chunkFiles;
    }

    /// <summary>
    /// Reads the row count the catalog recorded for a table.
    /// </summary>
    /// <param name="schemaName">The table's schema name.</param>
    /// <param name="tableName">The table's name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The count the table's manifest states, or <see langword="null"/> when the catalog holds
    /// no data folder for the table — which is what an export run without data writes.</returns>
    /// <exception cref="InterchangeFormatException">
    /// <c>catalog.json</c> names a folder for the table that the catalog does not contain.
    /// </exception>
    public async Task<long?> ReadTableRowCountAsync(string schemaName, string tableName, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var tableDir = ResolveTableDirectory(schemaName, tableName);
        if (tableDir is null)
        {
            return null;
        }

        var tableManifestPath = Path.Combine(tableDir, "table.json");
        if (!File.Exists(tableManifestPath))
        {
            return null;
        }

        var tableManifest = await JsonFile.ReadDocumentAsync<TableManifest>(tableManifestPath, ct);
        return tableManifest?.RowCount;
    }

    /// <summary>
    /// Measures every table of the catalog without reading a row of it: the row count the table's own
    /// <c>table.json</c> recorded, and the length of every file the table folder's <c>checksums.sha256</c>
    /// lists, its <c>data.*.csv</c> chunks and its <c>blobs/</c> payloads kept apart by that path prefix (FR3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The catalog records no length for a chunk or a blob — the manifest lists metadata files only — so the
    /// list the writer already produced is what says which files belong to a table, and the filesystem says how
    /// long each one is. Nothing is opened: a measure that had to read a chunk to answer could not run before
    /// the pass decided its layout.
    /// </para>
    /// <para>
    /// A table with no folder, or a folder written before the checksums list existed, still answers: the first
    /// is measured as zero bytes and zero rows, the second by the files that are there. The result keeps the
    /// order it was asked in, which is the load order the plan depends on (FR7).
    /// </para>
    /// </remarks>
    /// <param name="tables">The catalog's tables, in the order they are measured.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One measure per table, in the same order.</returns>
    internal async Task<IReadOnlyList<TableFootprint>> ReadTableFootprintsAsync(
        IReadOnlyList<TableDefinition> tables,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(tables);

        var footprints = new List<TableFootprint>(tables.Count);

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();

            var schemaName = table.SchemaRef.ObjectName();

            if (string.IsNullOrWhiteSpace(schemaName) || ResolveTableDirectory(schemaName, table.Name) is not { } tableDir)
            {
                footprints.Add(new TableFootprint(table, 0, 0, 0));
                continue;
            }

            var rows = await ReadTableRowCountAsync(schemaName, table.Name, ct) ?? 0;
            var (rowBytes, payloadBytes) = await MeasureTableAsync(tableDir, ct);

            footprints.Add(new TableFootprint(table, rows, rowBytes, payloadBytes));
        }

        return footprints;
    }

    /// <summary>
    /// Adds up one table folder's chunk bytes and payload bytes from the files the folder's own checksums list
    /// names.
    /// </summary>
    /// <param name="tableDir">The table's data folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The chunk bytes and the payload bytes.</returns>
    private static async Task<(long RowBytes, long PayloadBytes)> MeasureTableAsync(
        string tableDir,
        CancellationToken ct)
    {
        long rowBytes = 0;
        long payloadBytes = 0;
        var checksumsPath = Path.Combine(tableDir, "checksums.sha256");

        if (File.Exists(checksumsPath))
        {
            foreach (var line in await File.ReadAllLinesAsync(checksumsPath, ct))
            {
                // Format: <relative-path>\t<sha256>
                var tab = line.IndexOf('\t');

                if (tab <= 0)
                {
                    continue;
                }

                var relative = line[..tab];
                var path = Path.Combine(tableDir, relative.Replace('/', Path.DirectorySeparatorChar));
                var length = File.Exists(path) ? new FileInfo(path).Length : 0;

                Classify(relative, length, ref rowBytes, ref payloadBytes);
            }

            return (rowBytes, payloadBytes);
        }

        // A folder written before the checksums list existed: the files themselves are the list, in the same
        // two shapes — a chunk at the folder root, a payload under blobs/.
        foreach (var path in Directory.EnumerateFiles(tableDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(tableDir, path).Replace(Path.DirectorySeparatorChar, '/');

            Classify(relative, new FileInfo(path).Length, ref rowBytes, ref payloadBytes);
        }

        return (rowBytes, payloadBytes);
    }

    /// <summary>
    /// Sorts one listed file into the rows it holds or the payload it carries. Anything else a table folder
    /// holds — <c>table.json</c>, the checksums list itself — describes the data rather than being it.
    /// </summary>
    private static void Classify(string relative, long length, ref long rowBytes, ref long payloadBytes)
    {
        if (relative.StartsWith("data.", StringComparison.Ordinal)
            && relative.EndsWith(".csv", StringComparison.Ordinal)
            && !relative.Contains('/'))
        {
            rowBytes += length;
            return;
        }

        if (relative.StartsWith("blobs/", StringComparison.Ordinal))
        {
            payloadBytes += length;
        }
    }

    /// <summary>
    /// Resolves the folder a table's rows were written to.
    /// </summary>
    /// <remarks>
    /// <para>The folder is read from <c>catalog.json</c> rather than recomputed from the names. A folder name
    /// produced by collision resolution carries a discriminator tail derived from the sibling list the export
    /// saw, so asking <see cref="NameEncoder.EncodeSegment"/> again answers a different folder: on a
    /// case-insensitive filesystem the loser's plain name resolves onto the winner's directory, and the restore
    /// would re-insert the winner's rows while reading the winner's count and calling that a pass.</para>
    /// <para>The two absent cases stay distinct. A recorded folder that is not on disk is a catalog that
    /// contradicts itself, so it fails. A table with no entry at all is what a catalog written before folders
    /// were recorded, or an export run without data, looks like — the name is then derived the way it always
    /// was, and a missing folder means this table simply has no rows here.</para>
    /// </remarks>
    /// <param name="schemaName">The table's schema name.</param>
    /// <param name="tableName">The table's name.</param>
    /// <returns>The absolute folder path, or <see langword="null"/> when the catalog holds no data for the
    /// table.</returns>
    private string? ResolveTableDirectory(string schemaName, string tableName)
    {
        var dataRoot = Path.Combine(_catalogPath, InterchangeFormat.DataDirectory);

        if (TableFolders().TryGetValue((schemaName, tableName), out var recorded))
        {
            var recordedDir = Path.Combine(dataRoot, recorded.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(recordedDir))
            {
                throw new InterchangeFormatException(
                    $"catalog.json records the data folder \"{recorded}\" for the table " +
                    $"\"{schemaName}\".\"{tableName}\", but the catalog holds no such folder.");
            }

            return recordedDir;
        }

        var derived = Path.Combine(
            dataRoot,
            NameEncoder.EncodeSegment(schemaName),
            NameEncoder.EncodeSegment(tableName));
        return Directory.Exists(derived) ? derived : null;
    }

    /// <summary>
    /// Builds the table-to-folder map <c>catalog.json</c> carries, once per reader.
    /// </summary>
    private Dictionary<(string Schema, string Table), string> TableFolders()
    {
        if (_tableFolders is not null)
        {
            return _tableFolders;
        }

        IReadOnlyList<CatalogTableFolder> folders = _catalogIndex?.TableFolders ?? [];
        var map = new Dictionary<(string, string), string>(folders.Count);
        foreach (var entry in folders)
        {
            map[(entry.Schema, entry.Table)] = entry.Folder;
        }

        _tableFolders = map;
        return map;
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
