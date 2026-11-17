using CanonicalModel.Interchange;

using DatabaseInterchange.Format;
using DatabaseInterchange.Transform;

using DbAdapter.Definitions;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace DatabaseInterchange.Tests.Transform;

/// <summary>
/// Verifies that explicit blob extraction reads the v2 CSV row surface and preserves the stored bytes.
/// </summary>
public sealed class BlobExtractorTests : IDisposable
{
    private readonly string _catalogPath = Path.Combine(
        Path.GetTempPath(),
        "DbInterchangeBlobExtractor_" + Guid.NewGuid().ToString("N"));
    private readonly string _outputPath = Path.Combine(
        Path.GetTempPath(),
        "DbInterchangeBlobExtractorOutput_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath)) Directory.Delete(_catalogPath, recursive: true);
        if (Directory.Exists(_outputPath)) Directory.Delete(_outputPath, recursive: true);
    }

    [Fact]
    public async Task ExtractAsync_reads_csv_rows_and_writes_explicit_files()
    {
        var tableRef = ObjectRef.Table("public", "images");
        var table = new TableDefinition
        {
            Ref = tableRef,
            Name = "images",
            SchemaRef = ObjectRef.Schema("public"),
            Columns =
            [
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "images", "id"),
                    Name = "id",
                    TableRef = tableRef,
                    Ordinal = 0,
                    LogicalType = ELogicalType.Integer32,
                },
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "images", "payload"),
                    Name = "payload",
                    TableRef = tableRef,
                    Ordinal = 1,
                    LogicalType = ELogicalType.Binary,
                },
            ],
        };
        var bytes = WithPngSignature(100);

        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath,
                         producer: "DbClone-test",
                         sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("tables", [table]);
            await writer.WriteTableAsync(
                table,
                "public",
                RowsAsync(bytes),
                new ExportOptions());
            await writer.CompleteAsync();
        }

        var request = new BlobExtractionRequest(
            _catalogPath,
            "public",
            "images",
            _outputPath);
        var result = await new BlobExtractor(NullLogger<BlobExtractor>.Instance)
            .ExtractAsync(request, progress: null, CancellationToken.None);

        result.FilesExtracted.Should().Be(1);
        result.BytesExtracted.Should().Be(bytes.Length);
        File.ReadAllBytes(Path.Combine(_outputPath, "images_0.png"))
            .Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task ExtractAllAsync_uses_catalog_names_for_collision_resolved_folders()
    {
        var table = CreateBinaryTable("mixedcase");
        var bytes = WithPngSignature(100);

        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath,
                         producer: "DbClone-test",
                         sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("tables", [table]);
            await writer.WriteTableAsync(
                table,
                "public",
                RowsAsync(bytes),
                new ExportOptions(),
                resolvedTableName: "mixedcase~abc123");
            await writer.CompleteAsync();
        }

        var result = await new BlobExtractor(NullLogger<BlobExtractor>.Instance)
            .ExtractAllAsync(_catalogPath, _outputPath, progress: null, CancellationToken.None);

        result.FilesExtracted.Should().Be(1);
        File.ReadAllBytes(Path.Combine(_outputPath, "mixedcase_0.png"))
            .Should().BeEquivalentTo(bytes);
    }

    private static TableDefinition CreateBinaryTable(string tableName)
    {
        var tableRef = ObjectRef.Table("public", tableName);
        return new TableDefinition
        {
            Ref = tableRef,
            Name = tableName,
            SchemaRef = ObjectRef.Schema("public"),
            Columns =
            [
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", tableName, "id"),
                    Name = "id",
                    TableRef = tableRef,
                    Ordinal = 0,
                    LogicalType = ELogicalType.Integer32,
                },
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", tableName, "payload"),
                    Name = "payload",
                    TableRef = tableRef,
                    Ordinal = 1,
                    LogicalType = ELogicalType.Binary,
                },
            ],
        };
    }

    private static async IAsyncEnumerable<DataValue[]> RowsAsync(byte[] bytes)
    {
        yield return
        [
            DataValue.FromText(ELogicalType.Integer32, "1"),
            DataValue.FromText(ELogicalType.Binary, "\\x" + Convert.ToHexString(bytes)),
        ];

        await Task.CompletedTask;
    }

    private static byte[] WithPngSignature(int length)
    {
        var bytes = new byte[length];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4E;
        bytes[3] = 0x47;
        bytes[4] = 0x0D;
        bytes[5] = 0x0A;
        bytes[6] = 0x1A;
        bytes[7] = 0x0A;
        return bytes;
    }
}
