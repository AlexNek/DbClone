using DbAdapter.Definitions.Interchange;

using DatabaseInterchange.Format;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Format;

/// <summary>
/// The native section is written by <see cref="CatalogWriter"/> rather than by a caller reaching
/// <see cref="NativeObjectWriter"/> directly, because <c>manifest.json</c> is assembled from the
/// files the writer itself recorded. A file written around the writer would exist on disk, be read
/// at import, and carry no hash — the catalog would then claim completeness over content it never
/// verified.
/// </summary>
public sealed class CatalogWriterNativeSectionTests : IDisposable
{
    private readonly string _catalogPath;

    public CatalogWriterNativeSectionTests() =>
        _catalogPath = Path.Combine(
            Path.GetTempPath(),
            "DbInterchangeNativeTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
        {
            Directory.Delete(_catalogPath, recursive: true);
        }
    }

    [Fact]
    public async Task WriteNativeObjectsAsync_recordsTheFileInTheManifestSoVerificationCoversIt()
    {
        // Arrange
        var objects = new List<NativeObjectDefinition>
        {
            new("customType", "public", "mood", "CREATE TYPE public.mood AS ENUM ('sad');", "postgresql"),
            new("domain", "public", "positive_amount", "CREATE DOMAIN ...", "postgresql", ["numeric"]),
        };

        // Act
        await using (var writer = await CatalogWriter.CreateInterchangeAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            (await writer.WriteNativeObjectsAsync(objects)).Should().Be(2);
            await writer.CompleteAsync();
        }

        // Assert
        File.Exists(Path.Combine(_catalogPath, "native", "postgresql.json")).Should().BeTrue();

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        reader.Manifest.Files.Should().Contain(f => f.Path == "native/postgresql.json" && f.Bytes > 0);
        reader.Manifest.Sections.Should().NotContain("native");

        (await new CatalogVerifier().VerifyAsync(_catalogPath)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task WriteNativeObjectsAsync_roundTripsWhatTheSourceReported()
    {
        // Arrange
        var objects = new List<NativeObjectDefinition>
        {
            new("customType", "public", "mood", "CREATE TYPE public.mood AS ENUM ('sad', 'happy');", "postgresql"),
        };

        await using (var writer = await CatalogWriter.CreateInterchangeAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteNativeObjectsAsync(objects);
            await writer.CompleteAsync();
        }

        // Act
        var read = await new NativeObjectReader().ReadNativeObjectsAsync(_catalogPath, "postgresql");

        // Assert
        var native = read.Should().ContainSingle().Which;
        native.ObjectKind.Should().Be("customType");
        native.SchemaName.Should().Be("public");
        native.Name.Should().Be("mood");
        native.NativeSql.Should().Be("CREATE TYPE public.mood AS ENUM ('sad', 'happy');");
        native.SourceDialect.Should().Be("postgresql");
    }

    [Fact]
    public async Task WriteNativeObjectsAsync_withNothingToPreserve_writesNoFileAndListsNothing()
    {
        // Arrange
        await using var writer = await CatalogWriter.CreateInterchangeAsync(
            _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql");

        // Act
        (await writer.WriteNativeObjectsAsync([])).Should().Be(0);
        await writer.CompleteAsync();

        // Assert
        Directory.Exists(Path.Combine(_catalogPath, "native")).Should().BeFalse();

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        reader.Manifest.Files.Should().NotContain(f => f.Path.StartsWith("native/", StringComparison.Ordinal));
        NativeObjectReader.HasNativeObjects(_catalogPath, "postgresql").Should().BeFalse();
    }

    [Fact]
    public async Task CreateInterchangeAsync_recordsTheModelFormatAndDialectThatDecideTheImportPath()
    {
        // The path decision is made at import against these two fields, so an export must state
        // both: which vocabulary the sections are written in, and which engine they came from.
        await using (var writer = await CatalogWriter.CreateInterchangeAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.CompleteAsync();
        }

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);

        reader.Manifest.ModelFormat.Should().Be("interchange");
        reader.Manifest.SourceDialect.Should().Be("postgresql");
        reader.IsDirectFormat.Should().BeFalse();
    }
}
