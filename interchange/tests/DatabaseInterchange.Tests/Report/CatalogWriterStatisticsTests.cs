using System.Globalization;

using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DatabaseInterchange.Format;
using DatabaseInterchange.Report;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Report;

/// <summary>
/// The writer's five facts (D3) measured against the folder it just wrote. This is the only place the
/// report's numbers are checked against real files rather than against another model, and it is what
/// keeps FR3's three byte measures honest: a figure that disagrees with the folder is worse than none,
/// because the user is invited to compare them.
/// </summary>
public sealed class CatalogWriterStatisticsTests : IDisposable
{
    private readonly string _catalogPath = Path.Combine(
        Path.GetTempPath(),
        "DbInterchangeWriterStats_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
        {
            Directory.Delete(_catalogPath, recursive: true);
        }
    }

    [Fact]
    public async Task TableStatistics_StateTheChunkFilesAndTheirBytesAsTheFolderHoldsThem()
    {
        // Arrange — five rows at two rows per chunk: three files, and the writer counted them as it closed each.
        var capture = await CaptureAsync(TextTable("orders", 5));

        // Assert
        var chunks = ChunkFilesOf("orders");
        chunks.Should().HaveCount(3, "the chunk boundary is the writer's, and the report repeats its count");

        var stats = capture.Tables.Single();
        stats.Rows.Should().Be(5);
        stats.Chunks.Should().Be(chunks.Length);
        stats.RowBytes.Should().Be(chunks.Sum(Length), "row bytes are the data files, summed exactly");
        stats.BlobFiles.Should().Be(0);
        stats.BlobBytes.Should().Be(0);
    }

    [Fact]
    public async Task TableStatistics_KeepBlobBytesApartFromRowBytesAndFromBothMetadataFiles()
    {
        // Arrange
        var capture = await CaptureAsync(BlobTable("attachments", 3));

        // Assert — the two data measures add up the two file families, and neither of the table's own
        // metadata files is inside either of them (FR3, AC3). That is the assertion a folder-size
        // comparison would otherwise falsify.
        var stats = capture.Tables.Single();
        var chunks = ChunkFilesOf("attachments");
        var blobs = BlobFilesOf("attachments");
        blobs.Should().NotBeEmpty("a non-NULL binary value is stored outside the CSV");

        stats.Chunks.Should().Be(chunks.Length);
        stats.RowBytes.Should().Be(chunks.Sum(Length));
        stats.BlobFiles.Should().Be(blobs.Length);
        stats.BlobBytes.Should().Be(blobs.Sum(Length));
        stats.DataBytes.Should().Be(chunks.Sum(Length) + blobs.Sum(Length));

        var metadataBytes = TableFolderFilesOf("attachments")
            .Where(path => !chunks.Contains(path) && !blobs.Contains(path))
            .Select(Length)
            .Sum();
        metadataBytes.Should().BeGreaterThan(0, "the folder holds table.json and checksums.sha256");
        TableFolderFilesOf("attachments")
            .Sum(Length)
            .Should().Be(stats.DataBytes + metadataBytes, "the two data measures plus the two metadata files are the whole folder");
    }

    [Fact]
    public async Task TableStatistics_ForATableWithNoRows_ReportZeroChunksAndWriteNoChunkFile()
    {
        // Arrange — D3's zero-byte rule, read off the disk: the row writer finalizes a chunk only once a
        // row has been written, so an empty table produces no data file at all.
        var capture = await CaptureAsync(TextTable("orders", 0));

        // Assert
        ChunkFilesOf("orders").Should().BeEmpty();
        var stats = capture.Tables.Single();
        stats.Rows.Should().Be(0);
        stats.Chunks.Should().Be(0);
        stats.RowBytes.Should().Be(0);

        // The table is still a written table, which is what makes the report say `0 rows` and not
        // `no data captured` (FR10): the empty case is a fact about the table, not about the store.
        capture.Tables.Should().HaveCount(1);
    }

    [Fact]
    public async Task MetadataFileSizes_StateEveryManifestEntryOnce_AndNoDataFile()
    {
        // Arrange
        var capture = await CaptureAsync(BlobTable("attachments", 2));
        var manifest = await JsonFile.ReadDocumentAsync<ManifestFile>(
            Path.Combine(_catalogPath, InterchangeFormat.ManifestFileName), CancellationToken.None);

        // Assert — the projection is the manifest's own list, path for path, and it carries no chunk or
        // blob file: those are covered one level down by each table's checksums list (D3).
        var paths = capture.Metadata.Select(f => f.Path).ToList();
        paths.Should().BeEquivalentTo(manifest!.Files.Select(f => f.Path));
        paths.Distinct().Should().HaveCount(paths.Count, "one entry per file — a twice-listed file is a twice-counted total");
        paths.Should().OnlyContain(path => !path.EndsWith(".csv", StringComparison.Ordinal));
        paths.Should().NotContain(path => path.Contains("/blobs/", StringComparison.Ordinal));

        foreach (var (path, bytes) in capture.Metadata)
        {
            Length(Path.Combine(_catalogPath, path.Replace('/', Path.DirectorySeparatorChar)))
                .Should().Be(bytes, $"{path} is reported at the size the manifest hashes it at");
        }
    }

    [Fact]
    public async Task ExportedAt_IsTheInstantExportJsonCarries_NotAMomentAfterIt()
    {
        // Arrange
        var capture = await CaptureAsync(TextTable("orders", 1));

        // Assert — the report prints the writer's stamp, so `summary.md` and `export.json` cannot state two
        // export times a second apart (D3), which is what a reader of the header would assume.
        var export = await JsonFile.ReadDocumentAsync<ExportMetadata>(
            Path.Combine(_catalogPath, InterchangeFormat.ExportFileName), CancellationToken.None);
        capture.ExportedAt.Should().NotBeNull();
        capture.ExportedAt!.Value.ToUnixTimeSeconds()
            .Should().Be(export!.CreatedAt.ToUnixTimeSeconds(), "one instant is stamped once and read twice");
    }

    [Fact]
    public async Task ExportJson_StampsTheInstantToTheWholeSecond_SoTheCatalogBytesDoNotFollowTheClock()
    {
        // Arrange — the same source written twice, which is what a second store of one database is.
        var first = await CaptureAsync(TextTable("orders", 1));
        var firstStamp = Length(Path.Combine(_catalogPath, InterchangeFormat.ExportFileName));
        Directory.Delete(_catalogPath, recursive: true);

        var second = await CaptureAsync(TextTable("orders", 1));

        // Assert — the stamp's text is a fixed width: JsonSerializer trims the fraction's trailing zeros,
        // so a sub-second stamp would make export.json one to eight bytes longer or shorter from run to
        // run, and the catalog's own byte measures (FR3) are its facts, not a reading of the clock.
        var export = await JsonFile.ReadDocumentAsync<ExportMetadata>(
            Path.Combine(_catalogPath, InterchangeFormat.ExportFileName), CancellationToken.None);
        (export!.CreatedAt.Ticks % TimeSpan.TicksPerSecond).Should().Be(
            0, "the instant is stamped to the second, which is the resolution the report prints");
        Length(Path.Combine(_catalogPath, InterchangeFormat.ExportFileName)).Should().Be(firstStamp);
        second.RootJsonBytes.Should().Be(first.RootJsonBytes);
    }

    [Fact]
    public async Task CatalogBytes_AreDataPlusMetadata_AndEveryFileInTheFolderExceptTheReport()
    {
        // Arrange — a catalog that has data, blobs, sections and an empty table, plus the two report files
        // that must not be inside the measure (FR3: catalog bytes describe the artifact, not the courtesy).
        var capture = await CaptureAsync(
            BlobTable("attachments", 3),
            TextTable("orders", 0));
        await File.WriteAllTextAsync(Path.Combine(_catalogPath, "summary.md"), "# a report\n");
        await File.WriteAllTextAsync(Path.Combine(_catalogPath, "summary.md.tmp"), "scratch\n");

        var statistics = new CatalogStatisticsBuilder().Build(
            capture.Snapshot, capture.Tables, capture.Metadata, capture.RootJsonBytes,
            capture.Sections, capture.ExportedAt, "postgresql", "consistent snapshot", 0, []);

        // Assert
        var dataBytes = capture.Tables.Sum(t => t.DataBytes);
        var metadataBytes = capture.Metadata.Sum(f => f.Bytes) + capture.RootJsonBytes;
        statistics.DataBytes.Should().Be(dataBytes);
        statistics.MetadataBytes.Should().Be(metadataBytes, "the two root JSON files are metadata too (D3)");
        statistics.CatalogBytes.Should().Be(dataBytes + metadataBytes);

        var everyFile = Directory.GetFiles(_catalogPath, "*", SearchOption.AllDirectories);
        statistics.CatalogBytes.Should().Be(everyFile.Where(IsNotReport).Sum(Length));
        everyFile.Sum(Length).Should().BeGreaterThan(statistics.CatalogBytes,
            "the report is in the folder and deliberately not in the measure that describes it");
    }

    /// <summary>Writes a real catalog and hands back the facts the writer finished with (D3).</summary>
    private async Task<Capture> CaptureAsync(params (string Name, ELogicalType PayloadType, DataValue[][] Rows)[] tables)
    {
        var definitions = tables.Select(t => DataTable(t.Name, t.PayloadType)).ToArray();
        var snapshot = ReportModel.Snapshot(definitions);

        await using var writer = await CatalogWriter.CreateAsync(
            _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql");
        await writer.WriteSectionAsync("schemas", [ReportModel.Schema("public")]);
        await writer.WriteSectionAsync("tables", definitions);

        for (var index = 0; index < definitions.Length; index++)
        {
            await writer.WriteTableAsync(
                definitions[index], "public", RowsAsync(tables[index].Rows),
                new ExportOptions { MaxRowsPerChunk = 2 });
        }

        await writer.CompleteAsync();

        return new Capture(
            snapshot,
            writer.TableStatistics,
            writer.MetadataFileSizes,
            writer.RootJsonBytes,
            writer.ExportedAt,
            writer.SectionNames);
    }

    private static (string, ELogicalType, DataValue[][]) TextTable(string name, int rows) =>
        (name, ELogicalType.String, TextRows(rows));

    private static (string, ELogicalType, DataValue[][]) BlobTable(string name, int rows) =>
        (name, ELogicalType.Binary, BlobRows(rows));

    private static TableDefinition DataTable(string name, ELogicalType payloadType)
    {
        var tableRef = ObjectRef.Table("public", name);

        return new TableDefinition
        {
            Ref = tableRef,
            Name = name,
            SchemaRef = ObjectRef.Schema("public"),
            Columns =
            [
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", name, "id"),
                    Name = "id",
                    TableRef = tableRef,
                    Ordinal = 0,
                    LogicalType = ELogicalType.Integer32,
                },
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", name, "payload"),
                    Name = "payload",
                    TableRef = tableRef,
                    Ordinal = 1,
                    LogicalType = payloadType,
                },
            ],
        };
    }

    private static DataValue[][] TextRows(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new DataValue[]
        {
            DataValue.FromText(ELogicalType.Integer32, i.ToString(CultureInfo.InvariantCulture)),
            DataValue.FromText(ELogicalType.String, $"row {i}"),
        })];

    private static DataValue[][] BlobRows(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new DataValue[]
        {
            DataValue.FromText(ELogicalType.Integer32, i.ToString(CultureInfo.InvariantCulture)),
            DataValue.FromText(ELogicalType.Binary, "\\x" + Convert.ToHexString(Payload(i))),
        })];

    /// <summary>A payload with a real signature, so the container is classified rather than guessed.</summary>
    private static byte[] Payload(int seed)
    {
        var bytes = new byte[24 + seed];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4E;
        bytes[3] = 0x47;
        return bytes;
    }

    private static async IAsyncEnumerable<DataValue[]> RowsAsync(DataValue[][] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private string TableFolder(string table) =>
        Path.Combine(_catalogPath, InterchangeFormat.DataDirectory, "public", table);

    private string[] ChunkFilesOf(string table) => Directory.GetFiles(TableFolder(table), "data.*.csv");

    private string[] BlobFilesOf(string table) =>
        Directory.GetFiles(Path.Combine(TableFolder(table), "blobs"), "*", SearchOption.AllDirectories);

    private string[] TableFolderFilesOf(string table) =>
        Directory.GetFiles(TableFolder(table), "*", SearchOption.AllDirectories);

    private static long Length(string path) => new FileInfo(path).Length;

    private static bool IsNotReport(string path) =>
        !path.EndsWith("summary.md", StringComparison.OrdinalIgnoreCase)
        && !path.EndsWith("summary.md.tmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>What one writer run left behind: the model it wrote and the five facts it kept (D3).</summary>
    private sealed record Capture(
        InterchangeSnapshot Snapshot,
        IReadOnlyList<TableWriteStats> Tables,
        IReadOnlyList<(string Path, long Bytes)> Metadata,
        long RootJsonBytes,
        DateTimeOffset? ExportedAt,
        IReadOnlyList<string> Sections);
}
