using DatabaseInterchange.Format;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Format;

/// <summary>
/// The text half of design §6: the reference grammar (§6.1, §6.4's token shape) and the path rule
/// (§6.2). The byte-dependent half — the range fitting the container and the name matching the
/// bytes — is <see cref="BinaryFieldGrammarTests"/>, because that needs a file open.
/// </summary>
public sealed class BlobReferenceParserTests : IDisposable
{
    private readonly string _tableDir;

    public BlobReferenceParserTests()
    {
        _tableDir = Path.Combine(
            Path.GetTempPath(),
            "DbInterchangeBlobPathTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tableDir, "blobs"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tableDir))
        {
            Directory.Delete(_tableDir, recursive: true);
        }
    }

    [Fact]
    public void TryParseWellFormedReference_AnswersPathOffsetAndLength()
    {
        var ok = BlobReferenceParser.TryParse(
            "@blob:blobs/chunk-000000.image_data.png:4096:70128",
            out var reference);

        ok.Should().BeTrue();
        reference!.RelativePath.Should().Be("blobs/chunk-000000.image_data.png");
        reference.Offset.Should().Be(4096);
        reference.Length.Should().Be(70128);
    }

    [Fact]
    public void TryParseZeroLengthValue_IsStillAReferenceTheWriterCanProduce()
    {
        var ok = BlobReferenceParser.TryParse("@blob:blobs/chunk-000000.data.bin:0:0", out var reference);

        ok.Should().BeTrue("an empty container is a layout the writer can produce");
        reference!.Length.Should().Be(0);
    }

    [Theory]
    [InlineData("\\x414243")]
    [InlineData("a sentence beginning with the word blob")]
    [InlineData("")]
    public void TryParseFieldWithoutThePrefix_SaysNoAndInventsNothing(string field)
    {
        var ok = BlobReferenceParser.TryParse(field, out var reference);

        ok.Should().BeFalse();
        reference.Should().BeNull("a field that is not a reference must not be answered as one");
    }

    [Fact]
    public void TryParseMissingLengthSeparator_ReportsTheLengthSeparator()
    {
        var act = () => BlobReferenceParser.TryParse("@blob:blobs/a.png", out _);

        act.Should().Throw<InterchangeFormatException>().Which.Message
            .Should().Contain("missing length separator");
    }

    [Fact]
    public void TryParseMissingOffsetSeparator_ReportsTheOffsetSeparator()
    {
        var act = () => BlobReferenceParser.TryParse("@blob:blobs/a.png:5", out _);

        act.Should().Throw<InterchangeFormatException>().Which.Message
            .Should().Contain("missing offset separator");
    }

    [Theory]
    [InlineData("@blob:blobs/a.png:not-a-number:5")]
    [InlineData("@blob:blobs/a.png:-1:5")]
    public void TryParseInvalidOffset_ReportsTheOffsetItCouldNotRead(string field)
    {
        var act = () => BlobReferenceParser.TryParse(field, out _);

        act.Should().Throw<InterchangeFormatException>().Which.Message
            .Should().Contain("invalid offset");
    }

    [Theory]
    [InlineData("@blob:blobs/a.png:0:not-a-number")]
    [InlineData("@blob:blobs/a.png:0:-5")]
    public void TryParseInvalidLength_ReportsTheLengthItCouldNotRead(string field)
    {
        var act = () => BlobReferenceParser.TryParse(field, out _);

        act.Should().Throw<InterchangeFormatException>().Which.Message
            .Should().Contain("invalid length");
    }

    [Fact]
    public void TryParseEmptyPath_IsRefused()
    {
        var act = () => BlobReferenceParser.TryParse("@blob::0:5", out _);

        act.Should().Throw<InterchangeFormatException>().Which.Message
            .Should().Contain("empty path");
    }

    [Fact]
    public void TryParseRootedPath_IsRefusedAsNotRelative()
    {
        var act = () => BlobReferenceParser.TryParse("@blob:/etc/a.png:0:5", out _);

        act.Should().Throw<InterchangeFormatException>().Which.Message
            .Should().Contain("must be relative");
    }

    [Fact]
    public void TryParseDriveQualifiedPath_IsRefused()
    {
        // Which of the two rules catches it depends on how the host's path class treats a drive-relative
        // path — Windows answers "rooted", so the refusal is stated as "must be relative" there and as
        // "must not contain a drive letter" elsewhere. Either way the reference is refused.
        var act = () => BlobReferenceParser.TryParse("@blob:c:blobs/a.png:0:5", out _);

        act.Should().Throw<InterchangeFormatException>().Which.Message
            .Should().Contain("Blob reference path must");
    }

    [Theory]
    [InlineData("@blob:blobs/../secrets/a.png:0:5")]
    [InlineData("@blob:blobs\\..\\secrets\\a.png:0:5")]
    [InlineData("@blob:../blobs/a.png:0:5")]
    public void TryParseParentDirectorySegment_IsRefused(string field)
    {
        var act = () => BlobReferenceParser.TryParse(field, out _);

        act.Should().Throw<InterchangeFormatException>().Which.Message
            .Should().Contain("\"..\"");
    }

    [Fact]
    public void TryParsePathOutsideTheContainersDirectory_IsRefused()
    {
        // A reference may only address a container, never a row file, a table.json or a sibling's data.
        var act = () => BlobReferenceParser.TryParse("@blob:table.json:0:5", out _);

        act.Should().Throw<InterchangeFormatException>().Which.Message
            .Should().Contain("blobs/");
    }

    [Theory]
    [InlineData("blobs/a.png", "png")]
    [InlineData("blobs/a.PNG", "png")]
    [InlineData("blobs/a.parquet", "parquet")]
    [InlineData("blobs/a", "bin")]
    [InlineData("blobs/a.", "bin")]
    public void ExtractSuffix_ReadsTheNameLowerCaseOrFallsBack(string path, string expected) =>
        BlobReferenceParser.ExtractSuffix(path).Should().Be(expected);

    [Fact]
    public void ResolveContainerPathReferenceInsideTheTableFolder_AnswersTheContainersFile()
    {
        var path = BlobReferenceParser.ResolveContainerPath(
            new BlobRef("blobs/chunk-000000.data.png", 0, 10),
            _tableDir);

        path.Should().Be(Path.Combine(_tableDir, "blobs", "chunk-000000.data.png"));
    }

    [Fact]
    public void ResolveContainerPathCraftedParentSegment_IsRefusedBeforeAnythingOpens()
    {
        var act = () => BlobReferenceParser.ResolveContainerPath(
            new BlobRef("blobs/../../outside.png", 0, 10),
            _tableDir);

        act.Should().Throw<InterchangeFormatException>().Which.Message
            .Should().Contain("\"..\"");
    }

    [Fact]
    public void ResolveContainerPathReferenceThroughTheTablesOwnDirectory_IsTheOnlyShapeAccepted()
    {
        // Everything the path rule can be handed that is not under blobs/ inside this folder is refused
        // before a file is opened: the parent-segment and outside-directory refusals above, and
        // ValidatePath's rooted/drive rules, are what keep a crafted reference from reaching bytes this
        // table never wrote.
        var act = () => BlobReferenceParser.ResolveContainerPath(
            new BlobRef("checksums.sha256", 0, 10),
            _tableDir);

        act.Should().Throw<InterchangeFormatException>().Which.Message
            .Should().Contain("blobs/");
    }

    [Fact]
    public void ResolveContainerPathReachingAContainerThroughASymbolicLink_IsRefused()
    {
        // §6.2's second half: a link placed in blobs/ after the export would send the reads to bytes the
        // catalog never recorded, so the reparse point is refused as the corruption it is. Creating one
        // needs a privilege Windows withholds unless Developer Mode is on, so the test steps past that.
        var link = Path.Combine(_tableDir, "blobs", "chunk-000000.data.png");
        var target = Path.Combine(_tableDir, "..", "DbInterchangeLinkTarget_" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllText(target, "bytes the catalog never recorded");
            File.CreateSymbolicLink(link, target);
        }
        catch
        {
            return;
        }

        try
        {
            var act = () => BlobReferenceParser.ResolveContainerPath(
                new BlobRef("blobs/chunk-000000.data.png", 0, 5),
                _tableDir);

            act.Should().Throw<InterchangeFormatException>().Which.Message
                .Should().Contain("reparse point");
        }
        finally
        {
            foreach (var path in new[] { link, target })
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // The temp tree is removed by Dispose; a cleanup failure is not a test failure.
                }
            }
        }
    }
}
