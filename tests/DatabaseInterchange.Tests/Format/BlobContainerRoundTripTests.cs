using System.Runtime.CompilerServices;
using System.Security.Cryptography;

using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DatabaseInterchange.Format;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Format;

/// <summary>
/// The round trips part B's 1B checklist rows claim: a column holding several kinds of large value
/// leaves one container per kind, the row file carries a reference instead of hex, and reading the
/// table back hands over the original bytes. Everything here is hermetic — no database, no engine.
/// </summary>
public sealed class BlobContainerRoundTripTests : IDisposable
{
    private static readonly byte[] PngValue = WithSignature(
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 100);

    private static readonly byte[] JpegValue = WithSignature([0xFF, 0xD8, 0xFF, 0xE0], 120);

    private static readonly byte[] JpegValue2 = WithSignature([0xFF, 0xD8, 0xFF, 0xDB], 64);

    private static readonly byte[] UnnamedValue = "a value carrying no file signature at all"u8.ToArray();

    private readonly string _catalogPath;

    public BlobContainerRoundTripTests()
    {
        _catalogPath = Path.Combine(
            Path.GetTempPath(),
            "DbInterchangeBlobRoundTrip_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
        {
            Directory.Delete(_catalogPath, recursive: true);
        }
    }

    [Fact]
    public async Task OneColumnHoldingThreeKindsOfLargeValue_ProducesOneContainerPerKind()
    {
        await ExportAsync(
            ImagesTable(Column("data", ELogicalType.Binary)),
            Row(1, PngValue),
            Row(2, JpegValue),
            Row(3, UnnamedValue));

        // Assert — one container per (chunk, column, detected format), named by what the bytes carried.
        var files = Directory.GetFiles(Path.Combine(TableDir("images"), "blobs"));
        files.Should().HaveCount(3);
        files.Select(Path.GetExtension).Should().BeEquivalentTo(".png", ".jpg", ".bin");

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        var restored = await ReadBinaryAsync(reader, "images");
        restored.Should().HaveCount(3);
        restored[0].Should().BeEquivalentTo(PngValue);
        restored[1].Should().BeEquivalentTo(JpegValue);
        restored[2].Should().BeEquivalentTo(UnnamedValue);
    }

    [Fact]
    public async Task TheRowFileCarriesTheReferenceAndNotTheHexOfTheValue()
    {
        await ExportAsync(
            ImagesTable(Column("data", ELogicalType.Binary)),
            Row(1, PngValue));

        var rowFile = File.ReadAllText(Path.Combine(TableDir("images"), "data.00001.csv"));
        rowFile.Should().Contain("@blob:blobs/data.00001.data.000000.png:0:" + PngValue.Length);
        rowFile.Should().NotContain(Convert.ToHexString(PngValue),
            "a value that earned a container must not also be inlined into the row file");
    }

    [Fact]
    public async Task TwoValuesOfOneKindGetOneFilePerValue()
    {
        await ExportAsync(
            ImagesTable(Column("data", ELogicalType.Binary)),
            Row(1, JpegValue),
            Row(2, JpegValue2));

        var files = Directory.GetFiles(Path.Combine(TableDir("images"), "blobs"));
        files.Should().HaveCount(2);
        files.Select(Path.GetFileName).Should().BeEquivalentTo(
            "data.00001.data.000000.jpg",
            "data.00001.data.000001.jpg");
        File.ReadAllBytes(Path.Combine(TableDir("images"), "blobs", "data.00001.data.000000.jpg"))
            .Should().BeEquivalentTo(JpegValue);
        File.ReadAllBytes(Path.Combine(TableDir("images"), "blobs", "data.00001.data.000001.jpg"))
            .Should().BeEquivalentTo(JpegValue2);

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        var restored = await ReadBinaryAsync(reader, "images");
        restored[0].Should().BeEquivalentTo(JpegValue);
        restored[1].Should().BeEquivalentTo(JpegValue2);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(1)]
    public async Task EveryNonNullBinaryValueIsStoredExternally(int valueLength)
    {
        await ExportAsync(
            ImagesTable(Column("data", ELogicalType.Binary)),
            Row(1, WithSignature([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], valueLength)));

        Directory.GetFiles(Path.Combine(TableDir("images"), "blobs"))
            .Should().ContainSingle();

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        var value = (await ReadRowsAsync(reader, "images"))[0][1];
        value.Binary.Should().NotBeNull();
        value.Value.Should().BeNull();
        (await ReadAllAsync(value.Binary!)).Should().HaveCount(valueLength);
    }

    [Fact]
    public async Task NullBinaryValuesDoNotCreateFiles_ButEveryNonNullValueDoes()
    {
        await ExportAsync(
            ImagesTable(Column("data", ELogicalType.Binary)),
            Row(1, WithSignature([0x89, 0x50, 0x4E, 0x47], 8)),
            Row(2, [0x01, 0x02, 0x03]),
            Row(3, null));

        Directory.GetFiles(Path.Combine(TableDir("images"), "blobs"))
            .Should().HaveCount(2);

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        var rows = await ReadRowsAsync(reader, "images");
        rows.Should().HaveCount(3);
        rows[0][1].Binary.Should().NotBeNull();
        rows[1][1].Binary.Should().NotBeNull();
        rows[2][1].IsNull.Should().BeTrue("a NULL binary value is still a NULL");
    }

    [Fact]
    public async Task ColumnsDifferingOnlyByCase_GetTwoContainerNamesAndNeitherOverwritesTheOther()
    {
        // Design §3: the container is named by the resolved segment, so the case pair cannot open one
        // physical file on a case-insensitive filesystem.
        var table = ImagesTable(
            Column("IMAGE_DATA", ELogicalType.Binary, ordinal: 1),
            Column("image_data", ELogicalType.Binary, ordinal: 2));

        await ExportAsync(table, new DataValue[]
        {
            DataValue.FromText(ELogicalType.Integer32, "1"),
            DataValue.FromText(ELogicalType.Binary, HexOf(JpegValue)),
            DataValue.FromText(ELogicalType.Binary, HexOf(PngValue)),
        });

        var names = Directory.GetFiles(Path.Combine(TableDir("images"), "blobs"))
            .Select(path => Path.GetFileName(path)!)
            .ToList();
        names.Should().HaveCount(2);
        names.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(2,
            "two columns differing only by case must not name the same container file");

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        var row = (await ReadRowsAsync(reader, "images"))[0];
        (await ReadAllAsync(row[1].Binary!)).Should().BeEquivalentTo(JpegValue);
        (await ReadAllAsync(row[2].Binary!)).Should().BeEquivalentTo(PngValue);
    }

    [Fact]
    public async Task TwoRunsOverTheSameValues_NameTheSameContainers()
    {
        // The naming table is code, so this is a compile-time property — pinned anyway, because a change that
        // made a name depend on anything but (chunk, resolved column, detected format) would silently move every
        // reference in an existing catalog.
        var table = ImagesTable(Column("data", ELogicalType.Binary));

        await ExportAsync(table, Row(1, PngValue), Row(2, JpegValue), Row(3, UnnamedValue));
        var firstRun = ContainerNames();

        Directory.Delete(_catalogPath, recursive: true);
        await ExportAsync(table, Row(1, PngValue), Row(2, JpegValue), Row(3, UnnamedValue));

        ContainerNames().Should().Equal(firstRun);
    }

    [Fact]
    public async Task ContainerNamesComeFromTheEncodingRulesNotFromPatternMatching()
    {
        // Design §3, and the E5 correction: a column whose name already looks like a file name, a name
        // holding a dot, a reserved device name and a case pair all have to produce containers that open
        // and hold their own bytes. Nothing may split these names on a dot to find a suffix.
        var table = ImagesTable(
            Column("image_data.jpg", ELogicalType.Binary, ordinal: 1),
            Column("a.b", ELogicalType.Binary, ordinal: 2),
            Column("nul", ELogicalType.Binary, ordinal: 3),
            Column("image_data", ELogicalType.Binary, ordinal: 4),
            Column("IMAGE_DATA", ELogicalType.Binary, ordinal: 5));

        await ExportAsync(table, new DataValue[]
        {
            DataValue.FromText(ELogicalType.Integer32, "1"),
            DataValue.FromText(ELogicalType.Binary, HexOf(PngValue)),
            DataValue.FromText(ELogicalType.Binary, HexOf(JpegValue)),
            DataValue.FromText(ELogicalType.Binary, HexOf(UnnamedValue)),
            DataValue.FromText(ELogicalType.Binary, HexOf(JpegValue2)),
            DataValue.FromText(ELogicalType.Binary, HexOf(PngValue)),
        });

        var names = Directory.GetFiles(Path.Combine(TableDir("images"), "blobs"))
            .Select(path => Path.GetFileName(path)!)
            .ToList();
        names.Should().HaveCount(5);
        names.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(5,
            "five columns whose names collide in the ways §3 warns about must not share a container");
        names.Should().Contain(n => n.StartsWith("data.00001.~nul.", StringComparison.Ordinal),
            "the device name is escaped before the suffix is appended, so it cannot end up named 'nul.jpg'");

        // A path carrying dots resolves by the stored path alone: the value under `a.b` is read back from
        // its own container, not looked up by a name that was split at the first dot.
        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        var row = (await ReadRowsAsync(reader, "images"))[0];
        (await ReadAllAsync(row[1].Binary!)).Should().BeEquivalentTo(PngValue);
        (await ReadAllAsync(row[2].Binary!)).Should().BeEquivalentTo(JpegValue);
        (await ReadAllAsync(row[3].Binary!)).Should().BeEquivalentTo(UnnamedValue);
        (await ReadAllAsync(row[4].Binary!)).Should().BeEquivalentTo(JpegValue2);
        (await ReadAllAsync(row[5].Binary!)).Should().BeEquivalentTo(PngValue);
    }

    [Fact]
    public async Task EveryContainerAndRowFileIsListedInTheFoldersOwnChecksumFileWithAHashThatIsTrue()
    {
        await ExportAsync(
            ImagesTable(Column("data", ELogicalType.Binary)),
            Row(1, PngValue),
            Row(2, JpegValue),
            Row(3, UnnamedValue));

        var tableDir = TableDir("images");
        var listed = File.ReadAllLines(Path.Combine(tableDir, "checksums.sha256"))
            .Select(line => line.Split('\t'))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

        listed.Keys.Should().BeEquivalentTo(
            "data.00001.csv",
            "blobs/data.00001.data.000000.png",
            "blobs/data.00001.data.000001.jpg",
            "blobs/data.00001.data.000002.bin");

        // Part A/R8: a hash is recorded by the pass that wrote the file, so it must be true of the bytes
        // that landed on disk — otherwise the chain manifest → checksums → files is decoration.
        foreach (var (path, hash) in listed)
        {
            var file = Path.Combine(tableDir, path.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(file).Should().BeTrue($"{path} is listed, so it has to be stored");
            await using var stream = File.OpenRead(file);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            actual.Should().Be(hash, $"the recorded hash of {path} must answer the file it names");
        }
    }

    [Fact]
    public async Task TheFormatCountsTheCatalogRecordsAnswerTheValuesThatCrossedTheThreshold()
    {
        await ExportAsync(
            ImagesTable(Column("data", ELogicalType.Binary)),
            Row(1, PngValue),
            Row(2, JpegValue),
            Row(3, JpegValue2),
            Row(4, UnnamedValue),
            Row(5, WithSignature([0x89, 0x50, 0x4E, 0x47], 8)));

        var manifest = await JsonFile.ReadDocumentAsync<TableManifest>(
            Path.Combine(TableDir("images"), "table.json"),
            CancellationToken.None);

        manifest!.BlobFormatCounts["data"].Should().BeEquivalentTo(
            new Dictionary<string, int> { ["png"] = 1, ["jpg"] = 2, ["bin"] = 2 });

        // The map records every non-NULL binary value written to an individual file.
        // the rows. Asserting the key set is inside that vocabulary is what pins "bounded by the format itself".
        var declaredSuffixes = BlobFormat.Formats.Select(e => e.Suffix).Append(BlobFormat.Fallback).ToList();
        manifest.BlobFormatCounts["data"].Values.Sum()
            .Should().Be(5, "one count per non-NULL binary value");
        manifest.BlobFormatCounts["data"].Keys.Should().BeSubsetOf(declaredSuffixes,
            "a table.json map can only name what the naming table can produce");
    }

    [Fact]
    public async Task ACatalogHoldingContainersPassesVerification()
    {
        await ExportAsync(
            ImagesTable(Column("data", ELogicalType.Binary)),
            Row(1, PngValue),
            Row(2, JpegValue));

        var result = await new CatalogVerifier().VerifyAsync(_catalogPath);

        result.IsValid.Should().BeTrue(string.Join(" | ", result.Issues));
    }

    private static TableDefinition ImagesTable(params ColumnDefinition[] extraColumns)
    {
        var tableRef = ObjectRef.Table("public", "images");
        var columns = new List<ColumnDefinition>
        {
            new()
            {
                Ref = ObjectRef.Column("public", "images", "images_id"),
                Name = "id",
                TableRef = tableRef,
                Ordinal = 0,
                LogicalType = ELogicalType.Integer32,
            },
        };
        columns.AddRange(extraColumns);

        return new TableDefinition
        {
            Ref = tableRef,
            Name = "images",
            SchemaRef = ObjectRef.Schema("public"),
            Columns = columns,
        };
    }

    private static ColumnDefinition Column(
        string name,
        ELogicalType type,
        int ordinal = 1,
        string table = "images")
    {
        var tableRef = ObjectRef.Table("public", table);
        return new ColumnDefinition
        {
            Ref = ObjectRef.Column("public", table, $"{table}_{name}"),
            Name = name,
            TableRef = tableRef,
            Ordinal = ordinal,
            LogicalType = type,
        };
    }

    private static DataValue[] Row(int id, byte[]? binary) =>
    [
        DataValue.FromText(ELogicalType.Integer32, id.ToString()),
        binary is null
            ? DataValue.Null(ELogicalType.Binary)
            : DataValue.FromText(ELogicalType.Binary, HexOf(binary)),
    ];

    private static string HexOf(byte[] bytes) => "\\x" + Convert.ToHexString(bytes);

    private async Task ExportAsync(TableDefinition table, params DataValue[][] rows)
    {
        await using var writer = await CatalogWriter.CreateAsync(
            _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql");
        await writer.WriteSectionAsync("tables", [table]);
        await writer.WriteTableAsync(
            table,
            "public",
            RowsAsync(rows));
        await writer.CompleteAsync();
    }

    private string TableDir(string table) =>
        Path.Combine(_catalogPath, InterchangeFormat.DataDirectory, "public", table);

    private IReadOnlyList<string> ContainerNames() =>
        Directory.GetFiles(Path.Combine(TableDir("images"), "blobs"))
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static async IAsyncEnumerable<DataValue[]> RowsAsync(DataValue[][] rows)
    {
        foreach (var row in rows)
        {
            await Task.CompletedTask;
            yield return row;
        }
    }

    private static async Task<List<DataValue[]>> ReadRowsAsync(CatalogReader reader, string table)
    {
        var rows = new List<DataValue[]>();
        await foreach (var row in reader.ReadRowsAsync("public", table))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Reads the binary column of every row of a table, in row order, as bytes — from a container when
    /// the value earned one, from the inline token when it did not.
    /// </summary>
    private static async Task<IReadOnlyList<byte[]>> ReadBinaryAsync(CatalogReader reader, string table)
    {
        var values = new List<byte[]>();
        foreach (var row in await ReadRowsAsync(reader, table))
        {
            var value = row[1];
            if (value.Binary is not null)
            {
                values.Add(await ReadAllAsync(value.Binary));
            }
            else if (!value.IsNull)
            {
                values.Add(Convert.FromHexString(value.Value![2..]));
            }
        }

        return values;
    }

    private static async Task<byte[]> ReadAllAsync(IBinaryPayload payload)
    {
        await using var stream = payload.OpenRead();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static byte[] WithSignature(byte[] signature, int length)
    {
        var bytes = new byte[length];
        Buffer.BlockCopy(signature, 0, bytes, 0, Math.Min(signature.Length, length));
        return bytes;
    }
}
