using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DatabaseInterchange.Format;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Format;

/// <summary>
/// The rules design §6 states for the bytes behind a reference — §6.3 (the range and the threshold),
/// §6.4 (the two legal forms inside a Binary column), §6.5 (a reference only inside a Binary column),
/// §6.8 (the name against the signature) and §6.9 (the suffix never decides how a value is restored).
/// Each of them has its own message, and the tests below are the claim that every message a checklist
/// row promises is actually produced.
/// </summary>
public sealed class BinaryFieldGrammarTests : IDisposable
{
    private const int InlineThreshold = 32;

    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0];

    private readonly string _tableDir;

    public BinaryFieldGrammarTests()
    {
        _tableDir = Path.Combine(
            Path.GetTempPath(),
            "DbInterchangeGrammarTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task AReferenceVerifiedAgainstItsContainer_DecodesToAPayloadOverTheSameRange()
    {
        var bytes = WithLength(PngHeader, InlineThreshold * 2);
        WriteContainer("chunk-000000.data.png", bytes);

        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var row = await grammar.DecodeRowAsync(
            ["@blob:blobs/chunk-000000.data.png:0:" + bytes.Length],
            [ELogicalType.Binary],
            CancellationToken.None);

        var value = row[0];
        value.IsNull.Should().BeFalse();
        value.Value.Should().BeNull("the bytes are held outside the row file, never as text");
        value.Binary.Should().NotBeNull();
        value.Binary!.Length.Should().Be(bytes.Length);
        var restored = await ReadAllAsync(value.Binary);
        restored.Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task AReferenceToASecondRangeOfTheSameContainer_ReadsOnlyItsOwnBytes()
    {
        var first = WithLength(PngHeader, 40);
        var second = WithLength(JpegHeader, 40);
        var container = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, container, 0, first.Length);
        Buffer.BlockCopy(second, 0, container, first.Length, second.Length);
        WriteContainer("chunk-000000.data.bin", container);

        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var row = await grammar.DecodeRowAsync(
            [$"@blob:blobs/chunk-000000.data.bin:{first.Length}:{second.Length}"],
            [ELogicalType.Binary],
            CancellationToken.None);

        var restored = await ReadAllAsync(row[0].Binary!);
        restored.Should().BeEquivalentTo(second);
    }

    [Fact]
    public async Task TheUnnamedContainerRestoresExactlyLikeANamedOne()
    {
        // §6.9: the suffix is never an input to the restore. A bin container holding the same bytes a
        // png container holds produces the same value, the same way, with no branch on the name.
        var bytes = WithLength([0x01, 0x02, 0x03], InlineThreshold);
        WriteContainer("chunk-000000.data.bin", bytes);

        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var row = await grammar.DecodeRowAsync(
            [$"@blob:blobs/chunk-000000.data.bin:0:{bytes.Length}"],
            [ELogicalType.Binary],
            CancellationToken.None);

        var restored = await ReadAllAsync(row[0].Binary!);
        restored.Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task AReferenceInAColumnThatStoresText_IsRefusedRatherThanDereferenced()
    {
        // §6.5. The field is a user's sentence that happens to start with the prefix; resolving it would
        // put file bytes where the database held words.
        WriteContainer("chunk-000000.data.png", WithLength(PngHeader, InlineThreshold));

        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var act = () => grammar.DecodeRowAsync(
            ["@blob:blobs/chunk-000000.data.png:0:40"],
            [ELogicalType.String],
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<BlobRefInNonBinaryColumnException>();
        error.And.Message.Should().Contain("stores text").And.Contain("String");
    }

    [Fact]
    public async Task ABinaryFieldThatIsNeitherTokenNorReference_IsRefused()
    {
        // §6.4. Anything the writer could not have produced is corruption, not a value to guess at.
        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var act = () => grammar.DecodeRowAsync(
            ["plain words"],
            [ELogicalType.Binary],
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<MalformedBinaryTokenException>();
        error.And.Message.Should().Contain("neither NULL nor a blob reference");
    }

    [Theory]
    [InlineData("\\x414")]
    [InlineData("\\xZZ")]
    [InlineData("x4142")]
    public async Task ABinaryFieldShapedAlmostLikeTheToken_IsRefused(string field)
    {
        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var act = () => grammar.DecodeRowAsync(
            [field],
            [ELogicalType.Binary],
            CancellationToken.None);

        await act.Should().ThrowAsync<MalformedBinaryTokenException>();
    }

    [Theory]
    [InlineData("\\x")]
    [InlineData("\\x41424344")]
    public async Task ABinaryHexFieldIsRefusedBecauseEveryValueNeedsAFile(string token)
    {
        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var act = () => grammar.DecodeRowAsync(
            [token],
            [ELogicalType.Binary],
            CancellationToken.None);

        await act.Should().ThrowAsync<MalformedBinaryTokenException>();
    }

    [Fact]
    public async Task ANullField_AnswersANullOfItsColumnsType()
    {
        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var row = await grammar.DecodeRowAsync(
            [null],
            [ELogicalType.Binary],
            CancellationToken.None);

        row[0].IsNull.Should().BeTrue();
        row[0].LogicalType.Should().Be(ELogicalType.Binary);
    }

    [Fact]
    public async Task ARangeThatRunsPastTheEndOfTheContainer_IsRefused()
    {
        // §6.3, first half.
        var bytes = WithLength(PngHeader, 48);
        WriteContainer("chunk-000000.data.png", bytes);

        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var act = () => grammar.DecodeRowAsync(
            [$"@blob:blobs/chunk-000000.data.png:40:{bytes.Length}"],
            [ELogicalType.Binary],
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<InterchangeFormatException>();
        error.And.Message.Should().Contain("overruns container");
    }

    [Fact]
    public async Task ASmallReferencedBinaryFileIsAccepted()
    {
        WriteContainer("chunk-000000.data.png", WithLength(PngHeader, 16));

        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var row = await grammar.DecodeRowAsync(
            ["@blob:blobs/chunk-000000.data.png:0:16"],
            [ELogicalType.Binary],
            CancellationToken.None);

        row[0].Binary.Should().NotBeNull();
        row[0].Binary!.Length.Should().Be(16);
    }

    [Fact]
    public async Task AReferenceNamingAContainerThatIsNotThere_IsRefused()
    {
        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var act = () => grammar.DecodeRowAsync(
            ["@blob:blobs/chunk-000000.data.png:0:40"],
            [ELogicalType.Binary],
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<InterchangeFormatException>();
        error.And.Message.Should().Contain("Blob container not found");
    }

    [Fact]
    public async Task AContainerWhoseNameContradictsItsBytes_IsRefused()
    {
        // §6.8: the row file points at a .jpg whose bytes carry the PNG signature.
        WriteContainer("chunk-000000.data.jpg", WithLength(PngHeader, 48));

        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var act = () => grammar.DecodeRowAsync(
            ["@blob:blobs/chunk-000000.data.jpg:0:48"],
            [ELogicalType.Binary],
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<BlobFormatMismatchException>();
        error.And.Message.Should().Contain("named \"jpg\"");
    }

    [Fact]
    public async Task EveryRangeInsideAContainerNamedForItsSignatureCarriesThatSignature()
    {
        // The writer gives one container to one detected format, so §6.8 has to be true of every range in
        // a container it produced and not only of the file's first bytes — a probe anchored at the start
        // would let a container whose later values had been changed pass.
        var first = WithLength(JpegHeader, 40);
        var second = WithLength(JpegHeader, 48);
        var container = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, container, 0, first.Length);
        Buffer.BlockCopy(second, 0, container, first.Length, second.Length);
        WriteContainer("chunk-000000.data.jpg", container);

        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var row = await grammar.DecodeRowAsync(
            [
            $"@blob:blobs/chunk-000000.data.jpg:0:{first.Length}",
            $"@blob:blobs/chunk-000000.data.jpg:{first.Length}:{second.Length}",
        ],
            [ELogicalType.Binary, ELogicalType.Binary],
            CancellationToken.None);

        (await ReadAllAsync(row[0].Binary!)).Should().BeEquivalentTo(first);
        (await ReadAllAsync(row[1].Binary!)).Should().BeEquivalentTo(second);
    }

    [Fact]
    public async Task AMalformedReferenceIsRefusedByTheGrammarThatOwnsTheFieldType()
    {
        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var act = () => grammar.DecodeRowAsync(
            ["@blob:blobs/chunk-000000.data.png:0"],
            [ELogicalType.Binary],
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<InterchangeFormatException>();
        error.And.Message.Should().Contain("missing offset separator");
    }

    [Fact]
    public async Task ATextColumnMayHoldWhatLooksLikeTheHexToken_ItIsTextAndStaysText()
    {
        // The type scoping cuts both ways: \x… is only a binary token inside a Binary column, so a text
        // column can hold the literal backslash-x a user typed without it being re-read as bytes.
        await using var grammar = new BinaryFieldGrammar(_tableDir);
        var row = await grammar.DecodeRowAsync(
            ["\\x41"],
            [ELogicalType.String],
            CancellationToken.None);

        row[0].Value.Should().Be("\\x41");
    }

    private void WriteContainer(string fileName, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(_tableDir, "blobs", fileName), bytes);

    /// <summary>
    /// Grows a signature to a length, so a value is large enough to cross the threshold on purpose.
    /// </summary>
    private static byte[] WithLength(byte[] header, int length)
    {
        var bytes = new byte[length];
        Buffer.BlockCopy(header, 0, bytes, 0, Math.Min(header.Length, length));
        return bytes;
    }

    private static async Task<byte[]> ReadAllAsync(IBinaryPayload payload)
    {
        await using var stream = payload.OpenRead();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
