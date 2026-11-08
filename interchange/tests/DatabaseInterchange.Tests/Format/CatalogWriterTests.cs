using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DatabaseInterchange.Format;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Format;

/// <summary>
/// Hermetic round-trip tests for catalog writing: a catalog is written to a temp
/// directory with <see cref="CatalogWriter"/> and read back with
/// <see cref="CatalogReader"/>. No database connection is opened.
/// </summary>
public sealed class CatalogWriterTests : IDisposable
{
    private readonly string _catalogPath;

    public CatalogWriterTests()
    {
        _catalogPath = Path.Combine(
            Path.GetTempPath(),
            "DbInterchangeTests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
            Directory.Delete(_catalogPath, recursive: true);
    }

    [Fact]
    public async Task CompleteAsync_writes_manifest_catalog_and_section_files()
    {
        // Arrange
        var schemaRef = ObjectRef.Schema("public");
        var schemas = new List<SchemaDefinition>
        {
            new() { Ref = schemaRef, Name = "public" },
        };

        // Act
        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("schemas", schemas);
            await writer.CompleteAsync();
        }

        // Assert
        File.Exists(Path.Combine(_catalogPath, InterchangeFormat.ManifestFileName))
            .Should().BeTrue("the manifest is what makes a directory a catalog");
        File.Exists(Path.Combine(_catalogPath, InterchangeFormat.CatalogFileName))
            .Should().BeTrue();
        File.Exists(Path.Combine(
            _catalogPath, InterchangeFormat.SchemaDirectory, "schemas.json")).Should().BeTrue();
    }

    [Fact]
    public async Task Section_written_by_CatalogReader_is_read_back_with_same_values()
    {
        // Arrange
        var schemaRef = ObjectRef.Schema("public");
        var tableRef = ObjectRef.Table("public", "customers");
        var schemas = new List<SchemaDefinition> { new() { Ref = schemaRef, Name = "public" } };
        var tables = new List<TableDefinition>
        {
            new()
            {
                Ref = tableRef,
                Name = "customers",
                SchemaRef = schemaRef,
                Columns =
                [
                    new ColumnDefinition
                    {
                        Ref = ObjectRef.Column("public", "customers", "customers_id"),
                        Name = "id",
                        TableRef = tableRef,
                        Ordinal = 0,
                        LogicalType = ELogicalType.Decimal,
                        Nullable = false,
                    },
                    new ColumnDefinition
                    {
                        Ref = ObjectRef.Column("public", "customers", "customers_name"),
                        Name = "name",
                        TableRef = tableRef,
                        Ordinal = 1,
                        LogicalType = ELogicalType.String,
                        Nullable = true,
                    },
                ],
            },
        };

        // Act
        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("schemas", schemas);
            await writer.WriteSectionAsync("tables", tables);
            await writer.CompleteAsync();
        }

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        var readSchemas = await reader.ReadSectionAsync<SchemaDefinition>("schemas");
        var readTables = await reader.ReadSectionAsync<TableDefinition>("tables");

        // Assert
        readSchemas.Should().ContainSingle().Which.Name.Should().Be("public");
        var readTable = readTables.Should().ContainSingle().Which;
        readTable.Name.Should().Be("customers");
        readTable.Columns.Should().HaveCount(2);
        readTable.Columns[0].Name.Should().Be("id");
        readTable.Columns[1].Name.Should().Be("name");

        reader.Manifest.Sections.Should().Contain("schemas").And.Contain("tables");
        reader.Manifest.ModelFormat.Should().Be("direct");
        reader.Manifest.SourceDialect.Should().Be("postgresql");
        reader.Manifest.Counts.Tables.Should().Be(1);
    }

    [Fact]
    public async Task WriteTableAsync_writes_row_chunks_and_counts_them_in_manifest()
    {
        // Arrange
        var schemaRef = ObjectRef.Schema("public");
        var tableRef = ObjectRef.Table("public", "orders");
        var table = new TableDefinition
        {
            Ref = tableRef,
            Name = "orders",
            SchemaRef = schemaRef,
            Columns =
            [
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "orders", "orders_id"),
                    Name = "id",
                    TableRef = tableRef,
                    Ordinal = 0,
                    LogicalType = ELogicalType.Decimal,
                },
            ],
        };

        // Act
        long written;
        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("tables", [table]);
            written = await writer.WriteTableAsync(table, "public", ThreeRows());
            await writer.CompleteAsync();
        }

        // Assert
        written.Should().Be(3);
        var dataDir = Path.Combine(_catalogPath, InterchangeFormat.DataDirectory, "public", "orders");
        Directory.Exists(dataDir).Should().BeTrue();
        File.Exists(Path.Combine(dataDir, "table.json")).Should().BeTrue();
        File.Exists(Path.Combine(dataDir, "chunk-000000.ndjson")).Should().BeTrue();

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        reader.Manifest.Counts.Rows.Should().Be(3);
        reader.Manifest.Counts.Chunks.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_refuses_an_existing_catalog_unless_overwrite_is_set()
    {
        // Arrange
        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.CompleteAsync();
        }

        // Act — re-creating the same catalog directory without overwrite
        var act = async () =>
        {
            await using var _ = await CatalogWriter.CreateAsync(
                _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql");
        };

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Overwrite mode replaces the catalog instead of throwing
        await using var replaced = await CatalogWriter.CreateAsync(
            _catalogPath, producer: "DbClone-test", overwrite: true, sourceDialect: "postgresql");
        Directory.Exists(_catalogPath).Should().BeTrue();
    }

    [Fact]
    public void ObjectRef_is_stable_and_deterministic()
    {
        // Arrange / Act
        var first = ObjectRef.Table("public", "customers");
        var second = ObjectRef.Table("public", "customers");
        var other = ObjectRef.Table("public", "orders");

        // Assert
        first.Should().Be(second);
        first.Should().NotBe(other);
        first.Value.Should().StartWith("table:");
    }

    private static async IAsyncEnumerable<DataValue[]> ThreeRows()
    {
        for (var i = 1; i <= 3; i++)
        {
            yield return [DataValue.FromText(ELogicalType.Decimal, i.ToString())];
        }

        await Task.CompletedTask;
    }
}
