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
    public async Task Index_key_columns_survive_the_round_trip_and_an_unstated_list_reads_as_none()
    {
        // The columns are the fact a reader prints and the DDL is the statement a restore replays, so the
        // list is stored beside the text rather than inside it. A folder from a producer that stated no
        // list must read back as "not stated" and not as an index over no columns — that is what lets an
        // older catalog keep showing the text it does hold.
        var schemaRef = ObjectRef.Schema("public");
        var tableRef = ObjectRef.Table("public", "orders");

        IndexDefinition Index(string name, IReadOnlyList<string>? columns) => new()
        {
            Ref = ObjectRef.SubObject("index", "public", "orders", name),
            Name = name,
            TableRef = tableRef,
            IsUnique = false,
            Columns = columns,
        };

        var tables = new List<TableDefinition>
        {
            new()
            {
                Ref = tableRef,
                Name = "orders",
                SchemaRef = schemaRef,
                Indexes = [Index("ix_orders_customer", ["customer_id", "placed_at"]), Index("ix_orders_old", null)],
            },
        };

        // Act
        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("tables", tables);
            await writer.CompleteAsync();
        }

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        var read = (await reader.ReadSectionAsync<TableDefinition>("tables")).Single();

        // Assert
        read.Indexes.Single(i => i.Name == "ix_orders_customer").Columns.Should()
            .Equal("customer_id", "placed_at");
        read.Indexes.Single(i => i.Name == "ix_orders_old").Columns.Should().BeNull();
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
        var rowFile = Path.Combine(dataDir, "data.00001.csv");
        File.Exists(rowFile).Should().BeTrue();
        File.ReadAllBytes(rowFile).Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        File.ReadAllText(rowFile).Should().StartWith("id\n");

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        reader.Manifest.MinimumReader.Should().Be(InterchangeFormat.MinimumReader);
        reader.Manifest.Data.Encoding.Should().Be("csv");
        reader.Manifest.Counts.Rows.Should().Be(3);
        reader.Manifest.Counts.Chunks.Should().Be(1);
    }

    [Fact]
    public async Task Reader_and_verifier_refuse_legacy_ndjson_catalogs()
    {
        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.CompleteAsync();
        }

        var manifestPath = Path.Combine(_catalogPath, InterchangeFormat.ManifestFileName);
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!
            .AsObject();
        manifest["formatVersion"] = 1;
        manifest["minimumReader"] = 1;
        manifest["data"]!["encoding"] = "ndjson";
        await File.WriteAllTextAsync(
            manifestPath,
            manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var open = () => CatalogReader.OpenAsync(_catalogPath);
        await open.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*version 1*");

        var verification = await new CatalogVerifier().VerifyAsync(_catalogPath);
        verification.IsValid.Should().BeFalse();
        verification.Issues.Should().Contain(issue => issue.Contains("legacy NDJSON"));

        manifest["formatVersion"] = InterchangeFormat.Version;
        manifest["minimumReader"] = InterchangeFormat.MinimumReader;
        await File.WriteAllTextAsync(
            manifestPath,
            manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        await open.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*legacy NDJSON*");
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

    [Fact]
    public async Task WriteTableAsync_respects_MaxRowsPerChunk_from_options()
    {
        // Arrange — 25 rows with a 10-row limit should produce 3 chunk files.
        var schemaRef = ObjectRef.Schema("public");
        var tableRef = ObjectRef.Table("public", "numbers");
        var table = new TableDefinition
        {
            Ref = tableRef,
            Name = "numbers",
            SchemaRef = schemaRef,
            Columns =
            [
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "numbers", "numbers_value"),
                    Name = "value",
                    TableRef = tableRef,
                    Ordinal = 0,
                    LogicalType = ELogicalType.Integer32,
                },
            ],
        };

        var options = new ExportOptions { MaxRowsPerChunk = 10 };

        // Act
        long written;
        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("tables", [table]);
            written = await writer.WriteTableAsync(table, "public", TwentyFiveRows(), options);
            await writer.CompleteAsync();
        }

        // Assert
        written.Should().Be(25);
        var dataDir = Path.Combine(_catalogPath, InterchangeFormat.DataDirectory, "public", "numbers");
        var chunkFiles = Directory.GetFiles(dataDir, "data.*.csv");
        chunkFiles.Should().HaveCount(3, "25 rows at 10 per chunk = ceil(25/10) = 3 chunks");
    }

    [Fact]
    public async Task WriteTableAsync_with_default_options_uses_builtin_limits()
    {
        // Arrange — 3 rows with default options (100k limit) should produce 1 chunk file.
        var schemaRef = ObjectRef.Schema("public");
        var tableRef = ObjectRef.Table("public", "small");
        var table = new TableDefinition
        {
            Ref = tableRef,
            Name = "small",
            SchemaRef = schemaRef,
            Columns =
            [
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "small", "small_id"),
                    Name = "id",
                    TableRef = tableRef,
                    Ordinal = 0,
                    LogicalType = ELogicalType.Decimal,
                },
            ],
        };

        // Act — no options passed (null default)
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
        var dataDir = Path.Combine(_catalogPath, InterchangeFormat.DataDirectory, "public", "small");
        Directory.GetFiles(dataDir, "data.*.csv").Should().HaveCount(1);
    }

    private static async IAsyncEnumerable<DataValue[]> TwentyFiveRows()
    {
        for (var i = 1; i <= 25; i++)
        {
            yield return [DataValue.FromText(ELogicalType.Integer32, i.ToString())];
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<DataValue[]> ThreeRows()
    {
        for (var i = 1; i <= 3; i++)
        {
            yield return [DataValue.FromText(ELogicalType.Decimal, i.ToString())];
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task WriteTableAsync_streaming_round_trips_through_CatalogReader()
    {
        // Arrange — rows with nulls, embedded newlines, and values that cross chunk boundaries.
        var schemaRef = ObjectRef.Schema("public");
        var tableRef = ObjectRef.Table("public", "mixed");
        var table = new TableDefinition
        {
            Ref = tableRef,
            Name = "mixed",
            SchemaRef = schemaRef,
            Columns =
            [
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "mixed", "mixed_id"),
                    Name = "id",
                    TableRef = tableRef,
                    Ordinal = 0,
                    LogicalType = ELogicalType.Integer32,
                },
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "mixed", "mixed_text"),
                    Name = "text",
                    TableRef = tableRef,
                    Ordinal = 1,
                    LogicalType = ELogicalType.String,
                    Nullable = true,
                },
            ],
        };

        // Act — write with MaxRowsPerChunk=5 to force multiple chunks, then read back.
        var options = new ExportOptions { MaxRowsPerChunk = 5 };
        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("tables", [table]);
            await writer.WriteTableAsync(table, "public", MixedRows(), options);
            await writer.CompleteAsync();
        }

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        var readRows = new List<DataValue[]>();
        await foreach (var row in reader.ReadRowsAsync("public", "mixed"))
        {
            readRows.Add(row);
        }

        // Assert
        readRows.Should().HaveCount(12);
        readRows[0][0].Value.Should().Be("1");
        readRows[0][1].IsNull.Should().BeFalse();
        readRows[1][1].IsNull.Should().BeTrue("row 2 has a null text value");
        readRows[2][1].Value.Should().Contain("\n", "embedded newline must survive the round-trip");

        // Multiple chunk files were created and all rows are accounted for
        var dataDir = Path.Combine(_catalogPath, InterchangeFormat.DataDirectory, "public", "mixed");
        Directory.GetFiles(dataDir, "data.*.csv").Should().HaveCount(3,
            "12 rows at 5 per chunk = ceil(12/5) = 3 chunks");
    }

    private static async IAsyncEnumerable<DataValue[]> MixedRows()
    {
        // 12 rows: normal, null, embedded newline, then 9 more to cross chunk boundaries.
        yield return new[]
        {
            DataValue.FromText(ELogicalType.Integer32, "1"),
            DataValue.FromText(ELogicalType.String, "hello"),
        };
        yield return new[]
        {
            DataValue.FromText(ELogicalType.Integer32, "2"),
            DataValue.Null(ELogicalType.String),
        };
        yield return new[]
        {
            DataValue.FromText(ELogicalType.Integer32, "3"),
            DataValue.FromText(ELogicalType.String, "line1\nline2"),
        };
        for (var i = 4; i <= 12; i++)
        {
            yield return new[]
            {
                DataValue.FromText(ELogicalType.Integer32, i.ToString()),
                DataValue.FromText(ELogicalType.String, $"row-{i}"),
            };
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task WriteTableAsync_with_resolvedTableName_detects_collision()
    {
        // Arrange — write one table with a resolved name, then try to write another with the
        // same resolved name. The second write must fail (the §2.4 data-loss fix).
        var schemaRef = ObjectRef.Schema("public");
        var table1 = new TableDefinition
        {
            Ref = ObjectRef.Table("public", "first"),
            Name = "first",
            SchemaRef = schemaRef,
            Columns = [new ColumnDefinition
            {
                Ref = ObjectRef.Column("public", "first", "first_id"),
                Name = "id", TableRef = ObjectRef.Table("public", "first"),
                Ordinal = 0, LogicalType = ELogicalType.Integer32,
            }],
        };

        await using var writer = await CatalogWriter.CreateAsync(
            _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql");
        await writer.WriteSectionAsync("tables", [table1]);

        var emptyRows = EmptyRows();
        await writer.WriteTableAsync(table1, "public", emptyRows, resolvedTableName: "first");

        // Act — a second table claiming the same resolved folder name must throw.
        var table2 = new TableDefinition
        {
            Ref = ObjectRef.Table("public", "second"),
            Name = "second",
            SchemaRef = schemaRef,
            Columns = [new ColumnDefinition
            {
                Ref = ObjectRef.Column("public", "second", "second_id"),
                Name = "id", TableRef = ObjectRef.Table("public", "second"),
                Ordinal = 0, LogicalType = ELogicalType.Integer32,
            }],
        };

        var act = async () => await writer.WriteTableAsync(
            table2, "public", EmptyRows(), resolvedTableName: "first");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*collision*");
    }

    [Fact]
    public async Task WriteTableAsync_two_resolved_names_produce_two_folders()
    {
        // Arrange — two tables with distinct resolved names produce two separate folders.
        var schemaRef = ObjectRef.Schema("public");
        var table1 = new TableDefinition
        {
            Ref = ObjectRef.Table("public", "MixedCase"),
            Name = "MixedCase",
            SchemaRef = schemaRef,
            Columns = [new ColumnDefinition
            {
                Ref = ObjectRef.Column("public", "MixedCase", "mixedcase_id"),
                Name = "id", TableRef = ObjectRef.Table("public", "MixedCase"),
                Ordinal = 0, LogicalType = ELogicalType.Integer32,
            }],
        };
        var table2 = new TableDefinition
        {
            Ref = ObjectRef.Table("public", "mixedcase"),
            Name = "mixedcase",
            SchemaRef = schemaRef,
            Columns = [new ColumnDefinition
            {
                Ref = ObjectRef.Column("public", "mixedcase", "mixedcase_id"),
                Name = "id", TableRef = ObjectRef.Table("public", "mixedcase"),
                Ordinal = 0, LogicalType = ELogicalType.Integer32,
            }],
        };

        // Act
        await using var writer = await CatalogWriter.CreateAsync(
            _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql");
        await writer.WriteSectionAsync("tables", [table1, table2]);
        await writer.WriteTableAsync(table1, "public", EmptyRows(), resolvedTableName: "MixedCase");
        await writer.WriteTableAsync(table2, "public", EmptyRows(), resolvedTableName: "mixedcase~abc123");
        await writer.CompleteAsync();

        // Assert — both folders exist side by side
        var schemaDir = Path.Combine(_catalogPath, InterchangeFormat.DataDirectory, "public");
        Directory.GetDirectories(schemaDir).Should().HaveCount(2,
            "two tables with case-colliding names produce two distinct folders");
    }

    [Fact]
    public async Task WriteTableAsync_caseCollidingResolvedNames_readBackReturnsEachTablesOwnRows()
    {
        // Two folders existing side by side is only half of the fix: the reader has to know which one a
        // name answers. A resolved folder carries a discriminator derived from the sibling list the export
        // saw, so recomputing it from the name sends the loser's read to the winner's chunk files — the
        // §2.4 loss re-appearing as a restore that silently inserts the wrong table's rows.
        var upper = SingleColumnTable("MixedCase");
        var lower = SingleColumnTable("mixedcase");

        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("tables", [upper, lower]);
            await writer.WriteTableAsync(upper, "public", OneRowWithId(1), resolvedTableName: "MixedCase");
            await writer.WriteTableAsync(lower, "public", OneRowWithId(2), resolvedTableName: "mixedcase~abc123");
            await writer.CompleteAsync();
        }

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        var upperIds = await IdsAsync(reader, "MixedCase");
        var lowerIds = await IdsAsync(reader, "mixedcase");

        upperIds.Should().Equal("1");
        lowerIds.Should().Equal(new[] { "2" },
            "the folder a resolved name wrote into is read from the catalog, not guessed");
    }

    private static TableDefinition SingleColumnTable(string name)
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
                    Ref = ObjectRef.Column("public", name, $"{name}_id"),
                    Name = "id",
                    TableRef = tableRef,
                    Ordinal = 0,
                    LogicalType = ELogicalType.Integer32,
                },
            ],
        };
    }

    private static async IAsyncEnumerable<DataValue[]> OneRowWithId(int id)
    {
        await Task.CompletedTask;
        yield return [DataValue.FromText(ELogicalType.Integer32, id.ToString())];
    }

    private static async Task<List<string>> IdsAsync(CatalogReader reader, string tableName)
    {
        var ids = new List<string>();
        await foreach (var row in reader.ReadRowsAsync("public", tableName))
        {
            ids.Add(row[0].Value!);
        }

        return ids;
    }

    private static async IAsyncEnumerable<DataValue[]> EmptyRows()
    {
        await Task.CompletedTask;
        yield break;
    }
}
