using CanonicalModel.Interchange;

using DatabaseInterchange.Format;

using DbAdapter.Definitions;

using Microsoft.Extensions.Logging;

namespace DatabaseInterchange.Transform;

/// <summary>
/// Extracts binary values from a catalog table to individual files.
/// </summary>
/// <remarks>
/// <para>
/// The extractor reads the catalog's CSV rows through <see cref="CatalogReader"/> and writes each binary
/// value to a separate file named <c>&lt;table&gt;_&lt;row-index&gt;.&lt;suffix&gt;</c>. The suffix comes from
/// the detected format in a blob container or from the magic bytes of an inline value.
/// </para>
/// <para>
/// This is the extract surface design §10 B6 describes. It is an explicit operation and is not part of store.
/// </para>
/// </remarks>
public sealed class BlobExtractor
{
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="BlobExtractor"/> class.</summary>
    public BlobExtractor(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Scans the catalog for all tables with binary columns and extracts their blobs.</summary>
    public async Task<BlobExtractionResult> ExtractAllAsync(
        string catalogPath,
        string outputDirectory,
        IProgress<BlobExtractionProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalogPath);
        ArgumentNullException.ThrowIfNull(outputDirectory);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dataDir = Path.Combine(catalogPath, InterchangeFormat.DataDirectory);

        if (!Directory.Exists(dataDir))
        {
            throw new InterchangeFormatException($"Data directory not found: {dataDir}");
        }

        await using var catalogReader = await CatalogReader.OpenAsync(catalogPath, ct);
        _logger.LogInformation("Scanning catalog for tables with blobs: {CatalogPath}", catalogPath);

        var tablesToProcess = new List<(string Schema, string Table)>();
        foreach (var tableFolder in catalogReader.Index.TableFolders)
        {
            ct.ThrowIfCancellationRequested();

            var tableDir = Path.Combine(
                dataDir,
                tableFolder.Folder.Replace('/', Path.DirectorySeparatorChar));
            var tableJsonPath = Path.Combine(tableDir, "table.json");

            if (!File.Exists(tableJsonPath))
            {
                continue;
            }

            var manifest = await JsonFile.ReadDocumentAsync<TableManifestWithBlobs>(tableJsonPath, ct)
                .ConfigureAwait(false);
            if (manifest is null)
            {
                continue;
            }

            var hasBinaryColumns = manifest.Columns.Any(c => c.LogicalType == ELogicalType.Binary);
            var hasBlobs = manifest.BlobFormatCounts.Count > 0;
            if (hasBinaryColumns || hasBlobs)
            {
                // Keep the logical names from catalog.json. The folder is only a physical path and may
                // contain an encoding or collision discriminator that is not part of the table name.
                tablesToProcess.Add((tableFolder.Schema, tableFolder.Table));
                _logger.LogDebug(
                    "Found table with blobs: {Schema}.{Table} (binary columns: {BinaryColumns}, blob formats: {BlobFormats})",
                    tableFolder.Schema,
                    tableFolder.Table,
                    manifest.Columns.Count(c => c.LogicalType == ELogicalType.Binary),
                    manifest.BlobFormatCounts.Count);
            }
        }

        if (tablesToProcess.Count == 0)
        {
            _logger.LogWarning("No tables with binary columns found in catalog {CatalogPath}", catalogPath);
            return new BlobExtractionResult(0, 0, 0, sw.Elapsed);
        }

        _logger.LogInformation("Found {TableCount} table(s) with blobs, extracting...", tablesToProcess.Count);

        var totalFiles = 0;
        var totalBytes = 0L;
        var totalRows = 0;

        foreach (var (schema, table) in tablesToProcess)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new BlobExtractionProgress(
                $"Extracting from {schema}.{table}",
                0,
                tablesToProcess.Count));

            var request = new BlobExtractionRequest(catalogPath, schema, table, outputDirectory);
            var result = await ExtractAsync(request, progress, ct).ConfigureAwait(false);

            totalFiles += result.FilesExtracted;
            totalBytes += result.BytesExtracted;
            totalRows += result.RowsProcessed;
        }

        _logger.LogInformation(
            "Blob extraction completed: {Files} files, {Bytes} bytes, {Rows} rows in {Elapsed}",
            totalFiles,
            totalBytes,
            totalRows,
            sw.Elapsed);

        return new BlobExtractionResult(totalFiles, totalBytes, totalRows, sw.Elapsed);
    }

    /// <summary>Extracts all binary values from a table to individual files.</summary>
    public async Task<BlobExtractionResult> ExtractAsync(
        BlobExtractionRequest request,
        IProgress<BlobExtractionProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var catalogReader = await CatalogReader.OpenAsync(request.CatalogPath, ct);
        var tableDir = ResolveTableDirectory(
            catalogReader,
            request.CatalogPath,
            request.Schema,
            request.Table);

        _logger.LogInformation(
            "Blob extraction started: catalog={CatalogPath} schema={Schema} table={Table} output={OutputDir}",
            request.CatalogPath,
            request.Schema,
            request.Table,
            request.OutputDirectory);

        var tableJsonPath = Path.Combine(tableDir, "table.json");
        if (!File.Exists(tableJsonPath))
        {
            throw new InterchangeFormatException($"table.json not found: {tableJsonPath}");
        }

        var tableManifest = await JsonFile.ReadDocumentAsync<TableManifest>(tableJsonPath, ct)
            .ConfigureAwait(false);
        if (tableManifest is null)
        {
            throw new InterchangeFormatException($"Failed to read table manifest: {tableJsonPath}");
        }

        var binaryColumns = tableManifest.Columns
            .Select((column, index) => new { Column = column, Index = index })
            .Where(x => x.Column.LogicalType == ELogicalType.Binary)
            .ToList();

        if (binaryColumns.Count == 0)
        {
            _logger.LogWarning("No binary columns found in table {Schema}.{Table}", request.Schema, request.Table);
            return new BlobExtractionResult(0, 0, 0, sw.Elapsed);
        }

        Directory.CreateDirectory(request.OutputDirectory);

        var totalExtracted = 0;
        var totalBytes = 0L;
        var rowIndex = 0;

        await foreach (var row in catalogReader.ReadRowsAsync(request.Schema, request.Table, ct))
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new BlobExtractionProgress(
                $"Processing row {rowIndex}",
                rowIndex,
                (int)Math.Min(tableManifest.RowCount, int.MaxValue)));

            foreach (var binaryColumn in binaryColumns)
            {
                if (binaryColumn.Index >= row.Length)
                {
                    continue;
                }

                var value = row[binaryColumn.Index];
                if (value.IsNull)
                {
                    continue;
                }

                string outputPath;
                long extractedBytes;
                if (value.Binary is not null)
                {
                    var suffix = value.Binary is BlobPayload payload ? payload.Suffix : "bin";
                    (outputPath, extractedBytes) = await ExtractPayloadAsync(
                        value.Binary,
                        rowIndex,
                        request.Table,
                        request.OutputDirectory,
                        suffix,
                        ct).ConfigureAwait(false);
                }
                else if (value.Value is not null && value.Value.StartsWith("\\x", StringComparison.Ordinal))
                {
                    (outputPath, extractedBytes) = ExtractInlineHex(
                        value.Value,
                        rowIndex,
                        request.Table,
                        request.OutputDirectory);
                }
                else
                {
                    throw new InterchangeFormatException(
                        $"Binary row {rowIndex} column {binaryColumn.Column.Name} has no extractable payload.");
                }

                totalExtracted++;
                totalBytes += extractedBytes;

                _logger.LogDebug(
                    "Extracted: {OutputPath} ({Bytes} bytes)",
                    outputPath,
                    extractedBytes);
            }

            rowIndex++;
        }

        sw.Stop();

        _logger.LogInformation(
            "Blob extraction complete: {Count} files, {Bytes} bytes in {Elapsed}ms",
            totalExtracted,
            totalBytes,
            sw.ElapsedMilliseconds);

        return new BlobExtractionResult(totalExtracted, totalBytes, rowIndex, sw.Elapsed);
    }

    private static string ResolveTableDirectory(
        CatalogReader catalogReader,
        string catalogPath,
        string schemaName,
        string tableName)
    {
        var folder = catalogReader.Index.TableFolders.FirstOrDefault(entry =>
            string.Equals(entry.Schema, schemaName, StringComparison.Ordinal)
            && string.Equals(entry.Table, tableName, StringComparison.Ordinal));

        var relativeFolder = folder is null
            ? Path.Combine(
                NameEncoder.EncodeSegment(schemaName),
                NameEncoder.EncodeSegment(tableName))
            : folder.Folder.Replace('/', Path.DirectorySeparatorChar);

        var tableDir = Path.Combine(
            catalogPath,
            InterchangeFormat.DataDirectory,
            relativeFolder);
        if (!Directory.Exists(tableDir))
        {
            throw new InterchangeFormatException(
                $"Table directory not found for \"{schemaName}\".\"{tableName}\": {tableDir}");
        }

        return tableDir;
    }

    private static async Task<(string Path, long Bytes)> ExtractPayloadAsync(
        IBinaryPayload payload,
        int rowIndex,
        string tableStem,
        string outputDir,
        string suffix,
        CancellationToken ct)
    {
        var outputPath = Path.Combine(outputDir, $"{tableStem}_{rowIndex}.{suffix}");
        await using var source = payload.OpenRead();
        await using var destination = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 81920, ct);
        return (outputPath, payload.Length);
    }

    private static (string Path, long Bytes) ExtractInlineHex(
        string hexValue,
        int rowIndex,
        string tableStem,
        string outputDir)
    {
        var hex = hexValue[2..];
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
        }

        var suffix = DetectFormatFromBytes(bytes);
        var outputPath = Path.Combine(outputDir, $"{tableStem}_{rowIndex}.{suffix}");
        File.WriteAllBytes(outputPath, bytes);

        return (outputPath, bytes.Length);
    }

    private static string DetectFormatFromBytes(byte[] bytes)
    {
        if (bytes.Length < 4) return "bin";

        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "png";
        }

        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "jpg";
        }

        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
        {
            return "gif";
        }

        if (bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46)
        {
            return "pdf";
        }

        return "bin";
    }

    /// <summary>Minimal table manifest used to discover binary columns during extraction.</summary>
    private sealed record TableManifest(
        int RowCount,
        TableManifestColumn[] Columns);

    private sealed record TableManifestColumn(
        string Name,
        ELogicalType LogicalType);

    /// <summary>Extended manifest used when scanning tables for extraction.</summary>
    private sealed record TableManifestWithBlobs(
        int RowCount,
        TableManifestColumn[] Columns,
        Dictionary<string, Dictionary<string, int>> BlobFormatCounts);
}
