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
    private readonly List<CatalogTableFolder> _tableFolders = [];
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
    /// Writes a schema section to the catalog using streaming JSON serialization.
    /// </summary>
    /// <typeparam name="T">The type of objects in the section.</typeparam>
    /// <param name="sectionName">The section name (e.g., "schemas", "tables").</param>
    /// <param name="objects">The objects to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Each object is serialized directly to the file stream via <see cref="System.Text.Json.Utf8JsonWriter"/>,
    /// avoiding the intermediate string allocation that <c>JsonSerializer.Serialize(graph)</c> → <c>WriteAllTextAsync</c>
    /// would require. This satisfies R4: sections stream, one object at a time.
    /// </remarks>
    public async Task WriteSectionAsync<T>(
        string sectionName,
        IReadOnlyList<T> objects,
        CancellationToken ct = default) where T : ISchemaObject
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        var fileName = $"{sectionName}.json";
        var filePath = Path.Combine(_catalogPath, InterchangeFormat.SchemaDirectory, fileName);
        var relativePath = $"{InterchangeFormat.SchemaDirectory}/{fileName}";

        // Stream the section directly to disk: header, sorted objects, footer.
        var sorted = objects.OrderBy(o => o.Ref.Value).ToList();
        await using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await using var writer = new System.Text.Json.Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                Encoder = CatalogJson.WriterOptions.Encoder,
            });

            writer.WriteStartObject();
            writer.WriteString("section", sectionName);
            writer.WriteNumber("formatVersion", InterchangeFormat.Version);
            writer.WriteNumber("count", sorted.Count);

            writer.WritePropertyName("objects");
            writer.WriteStartArray();
            foreach (var obj in sorted)
            {
                JsonSerializer.Serialize(writer, obj, CatalogJson.WriterOptions);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
            await writer.FlushAsync(ct);
        }

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
    /// <para>Streaming: each relationship is serialized directly to the file stream via
    /// <see cref="System.Text.Json.Utf8JsonWriter"/>, avoiding the intermediate string allocation.</para>
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

        var fileName = $"{sectionName}.json";
        var filePath = Path.Combine(_catalogPath, InterchangeFormat.SchemaDirectory, fileName);
        var relativePath = $"{InterchangeFormat.SchemaDirectory}/{fileName}";

        // Stream the section directly to disk.
        await using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await using var writer = new System.Text.Json.Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                Encoder = CatalogJson.WriterOptions.Encoder,
            });

            writer.WriteStartObject();
            writer.WriteString("section", sectionName);
            writer.WriteNumber("formatVersion", InterchangeFormat.Version);
            writer.WriteNumber("count", ordered.Count);

            writer.WritePropertyName("objects");
            writer.WriteStartArray();
            foreach (var rel in ordered)
            {
                JsonSerializer.Serialize(writer, rel, CatalogJson.WriterOptions);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
            await writer.FlushAsync(ct);
        }

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
    /// <para>Streaming: the security object is serialized directly to the file stream via
    /// <see cref="System.Text.Json.Utf8JsonWriter"/>, avoiding the intermediate string allocation.</para>
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

        var fileName = $"{sectionName}.json";
        var filePath = Path.Combine(_catalogPath, InterchangeFormat.SchemaDirectory, fileName);
        var relativePath = $"{InterchangeFormat.SchemaDirectory}/{fileName}";

        // Stream the section directly to disk.
        await using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await using var writer = new System.Text.Json.Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                Encoder = CatalogJson.WriterOptions.Encoder,
            });

            writer.WriteStartObject();
            writer.WriteString("section", sectionName);
            writer.WriteNumber("formatVersion", InterchangeFormat.Version);

            writer.WritePropertyName("object");
            JsonSerializer.Serialize(writer, security, CatalogJson.WriterOptions);

            writer.WriteEndObject();
            await writer.FlushAsync(ct);
        }

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

        return true;
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
        var filePath = Path.Combine(_catalogPath, InterchangeFormat.SchemaDirectory, "model.json");
        var relativePath = $"{InterchangeFormat.SchemaDirectory}/model.json";

        await using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await serializer.SerializeAsync(stream, model, ct);
            await stream.FlushAsync(ct);
        }

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
    /// <param name="options">Export options that control chunk size and row limits. When null, built-in defaults apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="resolvedTableName">
    /// The pre-resolved table folder name (after collision detection via
    /// <see cref="NameEncoder.ResolveCollisions"/>). When null, the name is derived from
    /// <see cref="NameEncoder.EncodeSegment"/> alone — which is the collision-prone path.
    /// </param>
    /// <returns>The number of rows written.</returns>
    /// <remarks>
    /// <para>Write-through: each row is serialized once and written immediately via
    /// <see cref="CsvRowWriter"/>. No chunk accumulation in memory.</para>
    /// <para>Collision detection: when <paramref name="resolvedTableName"/> is provided, the
    /// writer verifies that the target directory does not already exist for a different table.
    /// A collision is a fatal export error — it is better to fail than to silently overwrite.
    /// </para>
    /// <para>The folder the rows land in is recorded in <c>catalog.json</c> with the table's own
    /// name beside it. A resolved name cannot be re-derived by a reader: the discriminator tail comes
    /// from the sibling list this export saw, so the answer is written down once here rather than
    /// guessed at restore time.</para>
    /// </remarks>
    public async Task<long> WriteTableAsync(
        CanonicalModel.Interchange.TableDefinition table,
        string schemaName,
        IAsyncEnumerable<DataValue[]> rows,
        ExportOptions? options = null,
        CancellationToken ct = default,
        string? resolvedTableName = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(table);

        var encodedSchema = NameEncoder.EncodeSegment(table.Ref.SchemaName() ?? schemaName);
        var encodedTable = resolvedTableName ?? NameEncoder.EncodeSegment(table.Name);
        var tableDir = Path.Combine(_catalogPath, InterchangeFormat.DataDirectory, encodedSchema, encodedTable);

        // Collision detection: when the caller resolved names across siblings, a directory that
        // already exists means two different tables mapped to one folder — the §2.4 data loss.
        // This is fatal: we refuse to overwrite rather than silently losing a table.
        if (resolvedTableName is not null && Directory.Exists(tableDir))
        {
            throw new InvalidOperationException(
                $"Table folder collision detected: schema={schemaName}, resolvedName={encodedTable}, " +
                $"table={table.Name}, path={tableDir}. Another table already claimed this folder.");
        }

        Directory.CreateDirectory(tableDir);

        var rowCount = 0L;
        var columnNames = table.Columns.Select(c => c.Name).ToList();

        // Container families are named by the column's resolved segment, the same rule a table folder
        // gets: two columns differing only by case produce two segments and so two distinct families,
        // never two streams onto one physical file (design §3).
        IReadOnlyList<string> columnSegments = columnNames.Count == 0
            ? []
            : ResolveColumnSegments(table, schemaName, columnNames);

        await using (var rowWriter = new CsvRowWriter(tableDir, options, columnNames, columnSegments))
        {
            await foreach (var row in rows.WithCancellation(ct))
            {
                await rowWriter.WriteRowAsync(row, ct);
                rowCount++;
            }

            await rowWriter.FinishAsync(ct);

            _totalRows += rowCount;
            _totalChunks += rowWriter.Chunks.Count;

            // Blob containers are listed in the table folder's own checksums.sha256 and nowhere else:
            // adding them to manifest.json would put an array that grows with the data back into the
            // file every reader loads whole (design §8).

            // Write checksums.sha256 — the authoritative per-file list for this table folder.
            // Phase 4B: replaces the inline chunks[] array in table.json.
            await WriteChecksumsFileAsync(tableDir, rowWriter, encodedSchema, encodedTable, ct);

            // Write table.json — scalars and bounded maps only, no chunks[] array
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
                ChunkCount = rowWriter.Chunks.Count,
                ChunkBytes = options?.ChunkBytes ?? 32 * 1024 * 1024,
                MaxRowsPerChunk = options?.MaxRowsPerChunk ?? 100_000,
                BlobFormatCounts = rowWriter.BlobFormatCounts,
            };

            var tableManifestPath = Path.Combine(tableDir, "table.json");
            await WriteJsonFileAsync(tableManifestPath, tableManifest, ct);

            var fileInfo = new FileInfo(tableManifestPath);
            var hash = await ComputeSha256Async(tableManifestPath, ct);
            _fileEntries.Add(new ManifestFileEntry
            {
                Path = $"{InterchangeFormat.DataDirectory}/{encodedSchema}/{encodedTable}/table.json",
                Bytes = fileInfo.Length,
                Sha256 = hash,
            });
        }

        _tableFolders.Add(new CatalogTableFolder
        {
            Schema = schemaName,
            Table = table.Name,
            Folder = $"{encodedSchema}/{encodedTable}",
        });

        return rowCount;
    }

    /// <summary>
    /// Resolves each column's container path segment over the table's own sibling list.
    /// </summary>
    private static IReadOnlyList<string> ResolveColumnSegments(
        CanonicalModel.Interchange.TableDefinition table,
        string schemaName,
        IReadOnlyList<string> columnNames)
    {
        var refByName = new Dictionary<string, ObjectRef>(columnNames.Count, StringComparer.Ordinal);
        foreach (var column in table.Columns)
        {
            // A duplicated name is the same column seen twice; the first reference answers for it.
            refByName.TryAdd(column.Name, column.Ref);
        }

        var resolved = NameEncoder.ResolveCollisions(
            columnNames,
            name => refByName.TryGetValue(name, out var columnRef)
                ? columnRef
                : ObjectRef.Column(schemaName, table.Name, name));

        return columnNames.Select(name => resolved[name]).ToList();
    }

    /// <summary>
    /// Writes the <c>checksums.sha256</c> file for a table folder. The file contains one line
    /// per data file (row chunks and blob containers) with its relative path and SHA-256 hash.
    /// </summary>
    /// <remarks>
    /// Phase 4B: This file is the authoritative per-file list for restore. The reader discovers
    /// chunk files by parsing this file rather than reading a <c>chunks[]</c> array from <c>table.json</c>.
    /// </remarks>
    private async Task WriteChecksumsFileAsync(
        string tableDir,
        CsvRowWriter rowWriter,
        string encodedSchema,
        string encodedTable,
        CancellationToken ct)
    {
        var lines = new List<string>();

        // Add chunk files
        foreach (var chunk in rowWriter.Chunks)
        {
            lines.Add($"{chunk.File}\t{chunk.Sha256}");
        }

        // Add blob container files
        foreach (var blobContainer in rowWriter.BlobContainers)
        {
            lines.Add($"blobs/{blobContainer.FileName}\t{blobContainer.Sha256}");
        }

        // Sort by path for deterministic output
        lines.Sort(StringComparer.Ordinal);

        var checksumsPath = Path.Combine(tableDir, "checksums.sha256");
        await File.WriteAllLinesAsync(checksumsPath, lines, Encoding.UTF8, ct);

        // Add checksums file to the manifest
        var fileInfo = new FileInfo(checksumsPath);
        var hash = await ComputeSha256Async(checksumsPath, ct);
        _fileEntries.Add(new ManifestFileEntry
        {
            Path = $"{InterchangeFormat.DataDirectory}/{encodedSchema}/{encodedTable}/checksums.sha256",
            Bytes = fileInfo.Length,
            Sha256 = hash,
        });
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
            TableFolders = [.. _tableFolders],
        };

        var catalogPath = Path.Combine(_catalogPath, InterchangeFormat.CatalogFileName);
        await WriteJsonFileAsync(catalogPath, catalog, ct);

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

        await WriteJsonFileAsync(
            Path.Combine(_catalogPath, InterchangeFormat.ExportFileName),
            export,
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
            Data = new ManifestData { Encoding = "csv" },
            Counts = new ManifestCounts
            {
                Tables = _totalTables,
                Columns = _totalColumns,
                Rows = _totalRows,
                Chunks = _totalChunks,
            },
            Files = _fileEntries.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
        };

        await WriteJsonFileAsync(
            Path.Combine(_catalogPath, InterchangeFormat.ManifestFileName),
            manifest,
            ct);
    }

    /// <summary>
    /// Serializes one catalog document straight to its file stream.
    /// </summary>
    /// <remarks>
    /// Part A/R4 forbids a stored document from ever being one <see cref="string"/>: writing through
    /// <c>JsonSerializer.SerializeAsync</c> over the file's stream emits the document into that stream's own
    /// buffer instead of building the text in memory and copying it over, which is what the
    /// <c>Serialize</c> → <c>WriteAllTextAsync</c> chain did.
    /// </remarks>
    private static async Task WriteJsonFileAsync<T>(string path, T document, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, document, CatalogJson.WriterOptions, ct);
        await stream.FlushAsync(ct);
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

    /// <summary>
    /// Gets the data folder each table's rows were written to, keyed by the schema and table names the
    /// catalog itself states. Written once at export, consulted at restore: a folder name produced by
    /// collision resolution is not re-derivable from a name, so it is recorded rather than recomputed.
    /// Empty in a catalog written before this rule, whose folders the reader derives as it always did.
    /// </summary>
    public IReadOnlyList<CatalogTableFolder> TableFolders { get; init; } = [];
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
/// <remarks>
/// <para>Phase 4B: The manifest no longer carries a per-chunk array (which would grow with the database).
/// Instead, it holds scalars and bounded maps only. The authoritative per-file list lives in
/// <c>checksums.sha256</c> within the table folder.</para>
/// </remarks>
internal sealed record TableManifest
{
    public string Id { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<TableManifestColumn> Columns { get; init; } = [];
    public long RowCount { get; init; }
    public string RowOrder { get; init; } = string.Empty;

    /// <summary>Gets the number of chunk files (discovered via checksums.sha256 at restore).</summary>
    public int ChunkCount { get; init; }

    /// <summary>Gets the chunk size setting that produced this layout.</summary>
    public int ChunkBytes { get; init; }

    /// <summary>Gets the maximum rows per chunk setting that produced this layout.</summary>
    public int MaxRowsPerChunk { get; init; }

    /// <summary>
    /// Gets the per-Binary-column format count maps. Keyed by column name, each value maps
    /// format suffix (e.g. "jpg", "png", "bin") to count of values stored in containers.
    /// Bounded by the naming table size, not by the row count.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> BlobFormatCounts { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, int>>();
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
