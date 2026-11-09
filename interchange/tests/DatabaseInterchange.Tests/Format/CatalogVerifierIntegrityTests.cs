using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DatabaseInterchange.Format;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Format;

/// <summary>
/// Tests for the tenth integrity check: cross-referencing tables section with data folders.
/// This catches the §2.4 silent data loss where case-colliding names map to one folder.
/// </summary>
public sealed class CatalogVerifierIntegrityTests : IDisposable
{
    private readonly string _catalogPath;

    public CatalogVerifierIntegrityTests()
    {
        _catalogPath = Path.Combine(
            Path.GetTempPath(),
            "DbInterchangeTests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
        {
            Directory.Delete(_catalogPath, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyAsync_detects_table_in_section_without_data_folder()
    {
        // Arrange — write a catalog with a table in the section but no data folder.
        var schemaRef = ObjectRef.Schema("public");
        var tableRef = ObjectRef.Table("public", "orphan");
        var schemas = new List<SchemaDefinition> { new() { Ref = schemaRef, Name = "public" } };
        var tables = new List<TableDefinition>
        {
            new()
            {
                Ref = tableRef,
                Name = "orphan",
                SchemaRef = schemaRef,
                Columns = [new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "orphan", "orphan_id"),
                    Name = "id", TableRef = tableRef,
                    Ordinal = 0, LogicalType = ELogicalType.Integer32,
                }],
            },
        };

        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("schemas", schemas);
            await writer.WriteSectionAsync("tables", tables);
            // Deliberately not writing table data — the folder won't exist.
            await writer.CompleteAsync();
        }

        // Act
        var result = await new CatalogVerifier().VerifyAsync(_catalogPath);

        // Assert — the verifier should detect the orphan table.
        result.IsValid.Should().BeFalse("a table in the section without a data folder is a integrity violation");
        result.Issues.Should().Contain(i => i.Contains("orphan") && i.Contains("no data folder"));
    }

    [Fact]
    public async Task VerifyAsync_detects_data_folder_without_table_in_section()
    {
        // Arrange — write a catalog with a table and its data, then manually add an extra folder.
        var schemaRef = ObjectRef.Schema("public");
        var tableRef = ObjectRef.Table("public", "legit");
        var table = new TableDefinition
        {
            Ref = tableRef,
            Name = "legit",
            SchemaRef = schemaRef,
            Columns = [new ColumnDefinition
            {
                Ref = ObjectRef.Column("public", "legit", "legit_id"),
                Name = "id", TableRef = tableRef,
                Ordinal = 0, LogicalType = ELogicalType.Integer32,
            }],
        };

        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("tables", [table]);
            await writer.WriteTableAsync(table, "public", EmptyRows());
            await writer.CompleteAsync();
        }

        // Manually create an extra data folder that's not in the section.
        var extraFolder = Path.Combine(_catalogPath, InterchangeFormat.DataDirectory, "public", "extra");
        Directory.CreateDirectory(extraFolder);

        // Act
        var result = await new CatalogVerifier().VerifyAsync(_catalogPath);

        // Assert — the verifier should detect the unclaimed folder.
        result.IsValid.Should().BeFalse("a data folder without a table in the section is an integrity violation");
        result.Issues.Should().Contain(i => i.Contains("extra") && i.Contains("no table"));
    }

    [Fact]
    public async Task VerifyAsync_passes_when_section_and_folders_match()
    {
        // Arrange — write a catalog with matching section and data folders.
        var schemaRef = ObjectRef.Schema("public");
        var tableRef = ObjectRef.Table("public", "matched");
        var table = new TableDefinition
        {
            Ref = tableRef,
            Name = "matched",
            SchemaRef = schemaRef,
            Columns = [new ColumnDefinition
            {
                Ref = ObjectRef.Column("public", "matched", "matched_id"),
                Name = "id", TableRef = tableRef,
                Ordinal = 0, LogicalType = ELogicalType.Integer32,
            }],
        };

        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("tables", [table]);
            await writer.WriteTableAsync(table, "public", EmptyRows());
            await writer.CompleteAsync();
        }

        // Act
        var result = await new CatalogVerifier().VerifyAsync(_catalogPath);

        // Assert
        result.IsValid.Should().BeTrue("section and folders match");
    }

    [Fact]
    public async Task VerifyAsync_refusesARecordedFolderThatIsNotStored()
    {
        // Arrange — a table whose recorded folder is absent, with a same-stemmed folder left on disk.
        // The check that used to match a folder by name prefix answered this as a pass: "images" was
        // satisfied by "images-2", so a table whose data is gone and a folder belonging to nobody both
        // disappeared. Both are reported now.
        var table = SimpleTable("images");
        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("tables", [table]);
            await writer.WriteTableAsync(table, "public", EmptyRows());
            await writer.CompleteAsync();
        }

        var folder = Path.Combine(_catalogPath, InterchangeFormat.DataDirectory, "public", "images");
        Directory.Move(folder, folder + "-2");

        // Act
        var result = await new CatalogVerifier().VerifyAsync(_catalogPath);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Contains("images") && i.Contains("no data folder"));
        result.Issues.Should().Contain(i => i.Contains("images-2") && i.Contains("no table"));
    }

    [Fact]
    public async Task VerifyAsync_acceptsFoldersNamedByTheCollisionDiscriminator()
    {
        // The folder a case pair was written into is not re-derivable from its name, so the check has to
        // read the map catalog.json records. Recomputing it would report the loser as missing data.
        var upper = SimpleTable("MixedCase");
        var lower = SimpleTable("mixedcase");

        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("tables", [upper, lower]);
            await writer.WriteTableAsync(upper, "public", EmptyRows(), resolvedTableName: "MixedCase");
            await writer.WriteTableAsync(lower, "public", EmptyRows(), resolvedTableName: "mixedcase~abc123");
            await writer.CompleteAsync();
        }

        var result = await new CatalogVerifier().VerifyAsync(_catalogPath);

        result.IsValid.Should().BeTrue(string.Join(" | ", result.Issues));
    }

    [Fact]
    public async Task VerifyAsync_reportsAFileStoredButNotListedInTheFoldersChecksums()
    {
        var table = SimpleTable("images");
        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("tables", [table]);
            await writer.WriteTableAsync(table, "public", EmptyRows());
            await writer.CompleteAsync();
        }

        // A container left in the folder by an interrupted run: the chain manifest → checksums → files
        // only says something if the list is exactly the set of data files.
        var blobsDir = Path.Combine(
            _catalogPath, InterchangeFormat.DataDirectory, "public", "images", "blobs");
        Directory.CreateDirectory(blobsDir);
        File.WriteAllBytes(Path.Combine(blobsDir, "data.00001.data.png"), [0x89, 0x50, 0x4E, 0x47]);

        var result = await new CatalogVerifier().VerifyAsync(_catalogPath);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain(i =>
            i.Contains("blobs/data.00001.data.png") && i.Contains("not listed in checksums.sha256"));
    }

    [Fact]
    public async Task VerifyAsync_reportsAFileListedInTheChecksumsButNotStored()
    {
        var table = SimpleTable("images");
        await using (var writer = await CatalogWriter.CreateAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteSectionAsync("tables", [table]);
            await writer.WriteTableAsync(table, "public", SingleRow());
            await writer.CompleteAsync();
        }

        // The list has to be exactly the set of data files: a chunk the writer promised and the folder does
        // not hold is the same broken chain as a file nobody promised.
        var chunk = Path.Combine(
            _catalogPath, InterchangeFormat.DataDirectory, "public", "images", "data.00001.csv");
        File.Exists(chunk).Should().BeTrue("the fixture has to store the file this test removes");
        File.Delete(chunk);

        var result = await new CatalogVerifier().VerifyAsync(_catalogPath);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain(i =>
            i.Contains("data.00001.csv") && i.Contains("is not in the folder"));
    }

    private static TableDefinition SimpleTable(string name)
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

    private static async IAsyncEnumerable<DataValue[]> EmptyRows()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<DataValue[]> SingleRow()
    {
        await Task.CompletedTask;
        yield return [DataValue.FromText(ELogicalType.Integer32, "1")];
    }
}
