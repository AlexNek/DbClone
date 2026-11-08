using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CanonicalModel.Interchange;
using CanonicalModel.Models;
using DbAdapter.Definitions;
using DbAdapter.Definitions.Interchange;

namespace DatabaseInterchange.Format;

/// <summary>
/// Writes a database snapshot to a catalog directory.
/// Supports two modes:
/// - Direct: Serializes <see cref="DatabaseModel"/> as-is for same-type transfers
/// - Interchange: Uses neutral types for cross-database transfers
/// </summary>
public sealed class CatalogWriter : IAsyncDisposable
{
    private readonly string _catalogPath;
    private readonly string _producer;
    private readonly string _modelFormat;
    private readonly string _sourceDialect;
    private readonly List<ManifestFileEntry> _fileEntries = [];
    private readonly List<string> _sections = [];
    private int _totalTables;
    private int _totalColumns;
    private long _totalRows;
    private int _totalChunks;
    private bool _disposed;

    private CatalogWriter(string catalogPath, string producer, string modelFormat, string sourceDialect)
    {
        _catalogPath = catalogPath;
        _producer = producer;
        _modelFormat = modelFormat;
        _sourceDialect = sourceDialect;
    }

    /// <summary>
    /// Creates a new catalog writer for the specified directory.
    /// </summary>
    /// <param name="catalogPath">The catalog directory path.</param>
    /// <param name="producer">The producer identifier.</param>
    /// <param name="overwrite">Whether to overwrite an existing catalog.</param>
    /// <param name="sourceDialect">The source database dialect (e.g., "postgresql").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A new catalog writer.</returns>
    public static async Task<CatalogWriter> CreateAsync(
        string catalogPath,
        string producer,
        bool overwrite = false,
        string sourceDialect = "",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);

        if (Directory.Exists(catalogPath))
        {
            if (!overwrite)
            {
                throw new InvalidOperationException($"Catalog directory already exists: {catalogPath}");
            }
            Directory.Delete(catalogPath, recursive: true);
        }

        Directory.CreateDirectory(catalogPath);
        Directory.CreateDirectory(Path.Combine(catalogPath, InterchangeFormat.SchemaDirectory));
        Directory.CreateDirectory(Path.Combine(catalogPath, InterchangeFormat.DataDirectory));

        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();

        // Default to direct mode for same-type transfers
        return new CatalogWriter(catalogPath, producer, "direct", sourceDialect);
    }

    /// <summary>
    /// Creates a catalog writer configured for interchange (cross-database) mode.
    /// </summary>
    public static async Task<CatalogWriter> CreateInterchangeAsync(
        string catalogPath,
        string producer,
        string sourceDialect,
        bool overwrite = false,
        CancellationToken ct = default)
    {
        var writer = await CreateAsync(catalogPath, producer, overwrite, sourceDialect, ct);
        // Return a new writer with interchange format
        return new CatalogWriter(catalogPath, producer, "interchange", sourceDialect);
    }

    /// <summary>
    /// Writes a schema section to the catalog.
    /// </summary>
    /// <typeparam name="T">The type of objects in the section.</typeparam>
    /// <param name="sectionName">The section name (e.g., "schemas", "tables").</param>
    /// <param name="objects">The objects to write.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteSectionAsync<T>(
        string sectionName,
        IReadOnlyList<T> objects,
        CancellationToken ct = default) where T : ISchemaObject
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        var section = new SectionFile<T>
        {
            Section = sectionName,
            FormatVersion = InterchangeFormat.Version,
            Count = objects.Count,
            Objects = objects.OrderBy(o => o.Ref.Value).ToList(),
        };

        await WriteSectionFileAsync(sectionName, section, ct);

        if (sectionName == "tables")
        {
            _totalTables = objects.Count;
        }
        else if (sectionName == "columns")
        {
            _totalColumns = objects.Count;
        }
    }

    /// <summary>
    /// Writes the relationship section — the dependency edges the source reports.
    /// </summary>
    /// <param name="relationships">The edges captured from the source.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of edges written.</returns>
    /// <remarks>
    /// An edge is not an object: a relationship has no identity of its own, it is a pair of references, so
    /// it is not an <see cref="ISchemaObject"/> and cannot go through
    /// <see cref="WriteSectionAsync{T}"/>. It keeps the same header-and-array file shape as every
    /// list section though, which is why <see cref="CatalogReader.ReadSectionAsync{T}"/> reads it
    /// back with no new reader API.
    /// Order is the writer's choice, so the triple is sorted — a catalog whose edges came out in
    /// source order would not be byte-stable across two runs of the same database state.
    /// </remarks>
    public async Task<int> WriteRelationshipsAsync(
        IReadOnlyList<Relationship> relationships,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(relationships);

        const string sectionName = "relationships";

        var ordered = relationships
            .OrderBy(r => r.Type, StringComparer.Ordinal)
            .ThenBy(r => r.DependencyRef.Value)
            .ThenBy(r => r.DependentRef.Value)
            .ToList();

        var section = new SectionFile<Relationship>
        {
            Section = sectionName,
            FormatVersion = InterchangeFormat.Version,
            Count = ordered.Count,
            Objects = ordered,
        };

        await WriteSectionFileAsync(sectionName, section, ct);
        return ordered.Count;
    }

    /// <summary>
    /// Writes the security section — principals, grants and row-level security policies.
    /// </summary>
    /// <param name="security">The section captured from the source, if any.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when a file was written; an empty section writes nothing.</returns>
    /// <remarks>
    /// Security is one compound object rather than a list of same-kind objects, so its file carries
    /// the section header with a single <c>object</c> member instead of an <c>objects</c> array
    /// (refactors/04/interchangeFormatDesign.md). <see cref="CatalogReader.ReadSecurityAsync"/> is
    /// its counterpart.
    /// Skipping an empty section is deliberate. Roles and grants are not part of the metadata this
    /// block can read, so a file of three empty lists would state that the source has no roles — a
    /// claim nobody checked. An absent section says the true thing: nothing was captured.
    /// </remarks>
    public async Task<bool> WriteSecurityAsync(
        SecuritySection? security,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (security is null
            || (security.Roles.Count == 0
                && security.Grants.Count == 0
                && security.Policies.Count == 0))
        {
            return false;
        }

        const string sectionName = "security";

        var section = new SecuritySectionFile
        {
            Section = sectionName,
            FormatVersion = InterchangeFormat.Version,
            Object = security,
        };

        await WriteSectionFileAsync(sectionName, section, ct);
        return true;
    }

    /// <summary>
    /// Writes one section file under <c>schema/</c>, records its size and hash in the manifest, and
    /// declares the section in the index.
    /// </summary>
    private async Task WriteSectionFileAsync(
        string sectionName,
        object section,
        CancellationToken ct)
    {
        var fileName = $"{sectionName}.json";
        var filePath = Path.Combine(_catalogPath, InterchangeFormat.SchemaDirectory, fileName);
        var relativePath = $"{InterchangeFormat.SchemaDirectory}/{fileName}";

        var json = JsonSerializer.Serialize(section, CatalogJson.WriterOptions);
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, ct);

        var fileInfo = new FileInfo(filePath);
        var hash = await ComputeSha256Async(filePath, ct);

        _fileEntries.Add(new ManifestFileEntry
        {
            Path = relativePath,
            Bytes = fileInfo.Length,
            Sha256 = hash,
        });

        if (!_sections.Contains(sectionName))
        {
            _sections.Add(sectionName);
        }
    }

    /// <summary>
    /// Writes the objects that have no neutral representation to <c>native/&lt;dialect&gt;.json</c>.
    /// </summary>
    /// <param name="objects">The native-only definitions captured from the source.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of objects written; an empty list writes no file.</returns>
    public async Task<int> WriteNativeObjectsAsync(
        IReadOnlyList<NativeObjectDefinition> objects,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(objects);

        // No file rather than an empty one: a missing native section and an empty one mean the same
        // thing to the reader, and a catalog that captured nothing dialect-specific stays smaller.
        if (objects.Count == 0)
        {
            return 0;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(_sourceDialect);

        var filePath = await new NativeObjectWriter().WriteNativeObjectsAsync(
            _catalogPath,
            _sourceDialect,
            objects,
            ct);

        var fileInfo = new FileInfo(filePath);
        var hash = await ComputeSha256Async(filePath, ct);

        _fileEntries.Add(new ManifestFileEntry
        {
            Path = $"{InterchangeFormat.NativeDirectory}/{_sourceDialect}.json",
            Bytes = fileInfo.Length,
            Sha256 = hash,
        });

        // Deliberately not added to _sections: the verifier resolves a listed section as
        // schema/<section>.json, and this file lives under native/.
        return objects.Count;
    }

    /// <summary>
    /// Writes a <see cref="DatabaseModel"/> directly to the catalog (for same-type transfers).
    /// No conversion to interchange types — maximum fidelity, minimum overhead.
    /// </summary>
    /// <param name="model">The database model to write.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteDatabaseModelAsync(DatabaseModel model, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(model);

        var serializer = new DirectCatalogSerializer();
        var json = serializer.Serialize(model);
        var filePath = Path.Combine(_catalogPath, InterchangeFormat.SchemaDirectory, "model.json");
        var relativePath = $"{InterchangeFormat.SchemaDirectory}/model.json";

        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, ct);

        var fileInfo = new FileInfo(filePath);
        var hash = await ComputeSha256Async(filePath, ct);

        _fileEntries.Add(new ManifestFileEntry
        {
            Path = relativePath,
            Bytes = fileInfo.Length,
            Sha256 = hash,
        });

        if (!_sections.Contains("model"))
        {
            _sections.Add("model");
        }

        // Update counts from the model
        _totalTables = model.Tables.Count;
        _totalColumns = model.Tables.Sum(t => t.Columns.Count);
    }

    /// <summary>
    /// Writes table data (rows) to the catalog.
    /// </summary>
    /// <param name="table">The table definition.</param>
    /// <param name="schemaName">The name of the schema owning the table, as captured.</param>
    /// <param name="rows">The rows to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    public async Task<long> WriteTableAsync(
        CanonicalModel.Interchange.TableDefinition table,
        string schemaName,
        IAsyncEnumerable<DataValue[]> rows,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(table);

        var encodedSchema = NameEncoder.EncodeSegment(table.Ref.SchemaName() ?? schemaName);
        var encodedTable = NameEncoder.EncodeSegment(table.Name);
        var tableDir = Path.Combine(_catalogPath, InterchangeFormat.DataDirectory, encodedSchema, encodedTable);
        Directory.CreateDirectory(tableDir);

        var chunkIndex = 0;
        var rowCount = 0L;
        var chunkRows = new List<DataValue[]>();
        var chunkList = new List<TableChunkInfo>();
        var chunkBytes = 0;
        const int maxChunkBytes = 32 * 1024 * 1024; // 32 MiB
        const int maxRowsPerChunk = 100_000;

        await foreach (var row in rows.WithCancellation(ct))
        {
            chunkRows.Add(row);
            rowCount++;

            var rowJson = JsonSerializer.Serialize(row.Select(r => r.IsNull ? null : r.Value), CatalogJson.RowWriterOptions);
            chunkBytes += Encoding.UTF8.GetByteCount(rowJson) + 1; // +1 for newline

            if (chunkBytes >= maxChunkBytes || chunkRows.Count >= maxRowsPerChunk)
            {
                var chunkInfo = await FlushChunkAsync(tableDir, chunkIndex, chunkRows, ct);
                chunkList.Add(chunkInfo);
                chunkIndex++;
                chunkRows.Clear();
                chunkBytes = 0;
            }
        }

        // Flush remaining rows
        if (chunkRows.Count > 0)
        {
            var chunkInfo = await FlushChunkAsync(tableDir, chunkIndex, chunkRows, ct);
            chunkList.Add(chunkInfo);
            chunkIndex++;
        }

        _totalRows += rowCount;
        _totalChunks += chunkIndex;

        // Write table.json
        var tableManifest = new TableManifest
        {
            Id = table.Ref.Value,
            Schema = schemaName,
            Name = table.Name,
            Columns = table.Columns.Select(c => new TableManifestColumn
            {
                Name = c.Name,
                LogicalType = c.LogicalType.ToString(),
            }).ToList(),
            RowCount = rowCount,
            RowOrder = table.RowOrder.ToString(),
            Chunks = chunkList,
        };

        var tableJson = JsonSerializer.Serialize(tableManifest, CatalogJson.WriterOptions);
        var tableManifestPath = Path.Combine(tableDir, "table.json");
        await File.WriteAllTextAsync(tableManifestPath, tableJson, Encoding.UTF8, ct);

        var fileInfo = new FileInfo(tableManifestPath);
        var hash = await ComputeSha256Async(tableManifestPath, ct);
        _fileEntries.Add(new ManifestFileEntry
        {
            Path = $"{InterchangeFormat.DataDirectory}/{encodedSchema}/{encodedTable}/table.json",
            Bytes = fileInfo.Length,
            Sha256 = hash,
        });

        return rowCount;
    }

    private static async Task<TableChunkInfo> FlushChunkAsync(
        string tableDir,
        int index,
        List<DataValue[]> rows,
        CancellationToken ct)
    {
        var chunkPath = Path.Combine(tableDir, $"chunk-{index:D6}.ndjson");
        var sb = new StringBuilder();

        foreach (var row in rows)
        {
            var json = JsonSerializer.Serialize(row.Select(r => r.IsNull ? null : r.Value), CatalogJson.RowWriterOptions);
            sb.AppendLine(json);
        }

        await File.WriteAllTextAsync(chunkPath, sb.ToString(), Encoding.UTF8, ct);

        var fileInfo = new FileInfo(chunkPath);
        var hash = await ComputeSha256Async(chunkPath, ct);

        return new TableChunkInfo
        {
            File = $"chunk-{index:D6}.ndjson",
            Bytes = fileInfo.Length,
            Rows = rows.Count,
            Sha256 = hash,
        };
    }

    /// <summary>
    /// Completes the catalog by writing catalog.json, export.json, and manifest.json.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task CompleteAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Write catalog.json
        var catalog = new CatalogIndex
        {
            FormatVersion = InterchangeFormat.Version,
            Sections = _sections,
            TableCount = _totalTables,
        };

        var catalogJson = JsonSerializer.Serialize(catalog, CatalogJson.WriterOptions);
        var catalogPath = Path.Combine(_catalogPath, InterchangeFormat.CatalogFileName);
        await File.WriteAllTextAsync(catalogPath, catalogJson, Encoding.UTF8, ct);

        var catalogFileInfo = new FileInfo(catalogPath);
        var catalogHash = await ComputeSha256Async(catalogPath, ct);
        _fileEntries.Insert(0, new ManifestFileEntry
        {
            Path = InterchangeFormat.CatalogFileName,
            Bytes = catalogFileInfo.Length,
            Sha256 = catalogHash,
        });

        // Write export.json
        var export = new ExportMetadata
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Producer = _producer,
        };

        var exportJson = JsonSerializer.Serialize(export, CatalogJson.WriterOptions);
        await File.WriteAllTextAsync(
            Path.Combine(_catalogPath, InterchangeFormat.ExportFileName),
            exportJson,
            Encoding.UTF8,
            ct);

        // Write manifest.json last
        var manifest = new ManifestFile
        {
            FormatVersion = InterchangeFormat.Version,
            MinimumReader = InterchangeFormat.MinimumReader,
            Producer = _producer,
            Complete = true,
            ModelFormat = _modelFormat,
            SourceDialect = _sourceDialect,
            Sections = _sections,
            Counts = new ManifestCounts
            {
                Tables = _totalTables,
                Columns = _totalColumns,
                Rows = _totalRows,
                Chunks = _totalChunks,
            },
            Files = _fileEntries.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
        };

        var manifestJson = JsonSerializer.Serialize(manifest, CatalogJson.WriterOptions);
        await File.WriteAllTextAsync(
            Path.Combine(_catalogPath, InterchangeFormat.ManifestFileName),
            manifestJson,
            Encoding.UTF8,
            ct);
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

/// <summary>
/// A section file in the catalog.
/// </summary>
internal sealed record SectionFile<T>
{
    public string Section { get; init; } = string.Empty;
    public int FormatVersion { get; init; }
    public int Count { get; init; }
    public IReadOnlyList<T> Objects { get; init; } = [];
}

/// <summary>
/// The security section file: the same header as a list section, holding one compound object.
/// </summary>
internal sealed record SecuritySectionFile
{
    public string Section { get; init; } = string.Empty;
    public int FormatVersion { get; init; }
    public SecuritySection? Object { get; init; }
}

/// <summary>
/// The catalog.json index structure.
/// </summary>
public sealed record CatalogIndex
{
    public int FormatVersion { get; init; }
    public IReadOnlyList<string> Sections { get; init; } = [];
    public int TableCount { get; init; }
}

/// <summary>
/// The export.json metadata structure.
/// </summary>
internal sealed record ExportMetadata
{
    public DateTimeOffset CreatedAt { get; init; }
    public string Producer { get; init; } = string.Empty;
}

/// <summary>
/// Table manifest (table.json) structure.
/// </summary>
internal sealed record TableManifest
{
    public string Id { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<TableManifestColumn> Columns { get; init; } = [];
    public long RowCount { get; init; }
    public string RowOrder { get; init; } = string.Empty;
    public IReadOnlyList<TableChunkInfo> Chunks { get; init; } = [];
}

/// <summary>
/// Column information in table manifest.
/// </summary>
internal sealed record TableManifestColumn
{
    public string Name { get; init; } = string.Empty;
    public string LogicalType { get; init; } = string.Empty;
}

/// <summary>
/// Chunk information in table manifest.
/// </summary>
internal sealed record TableChunkInfo
{
    public string File { get; init; } = string.Empty;
    public long Bytes { get; init; }
    public int Rows { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}
