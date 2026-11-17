using System.Text;
using System.Text.Json;

using DatabaseInterchange.Format;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Format;

/// <summary>
/// Part A/R4 as the framer implements it: a section is read out of the file's bytes one element at a
/// time, so the document's text never exists at once. These are the cases the hand-written structural
/// walk can get wrong — an element that crosses a buffer refill, a name inside an element that repeats
/// the array's own name, brackets and escapes inside a string — plus the two endings a catalog can have.
/// </summary>
public sealed class JsonSectionStreamingTests : IDisposable
{
    private readonly string _dir;

    public JsonSectionStreamingTests()
    {
        _dir = Path.Combine(
            Path.GetTempPath(),
            "DbInterchangeStreaming_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task EveryElementOfTheArrayIsHandedOverInOrder()
    {
        var path = Write("""
            {"section":"tables","objects":[{"n":"a"},{"n":"b"},{"n":"c"}]}
            """);

        var names = new List<string>();
        await foreach (var element in JsonFile.ReadObjectsAsync<JsonElement>(path, "objects"))
        {
            names.Add(element.GetProperty("n").GetString()!);
        }

        names.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task ANestedArrayInsideAnElementIsPartOfTheElementAndNotTheEndOfTheSection()
    {
        var path = Write("""
            {"section":"columns","objects":[{"id":1,"parts":[10,20,30]},{"id":2,"parts":[]}]}
            """);

        var ids = new List<int>();
        await foreach (var element in JsonFile.ReadObjectsAsync<JsonElement>(path, "objects"))
        {
            ids.Add(element.GetProperty("id").GetInt32());
            element.GetProperty("parts").GetArrayLength().Should().BeGreaterThanOrEqualTo(0);
        }

        ids.Should().Equal([1, 2], "an element's own array must not be mistaken for the section's");
    }

    [Fact]
    public async Task APropertyNamedLikeTheArrayInsideAnElementDoesNotRedirectTheWalk()
    {
        // The framer recognises the array only as a direct member of the root object, so a stored object
        // that carries a property of the same spelling cannot make the reader skip or re-enter.
        var path = Write("""
            {"section":"tables","objects":[{"objects":[1,2],"n":"first"},{"n":"second"}]}
            """);

        var names = new List<string?>();
        await foreach (var element in JsonFile.ReadObjectsAsync<JsonElement>(path, "objects"))
        {
            names.Add(element.GetProperty("n").GetString());
        }

        names.Should().Equal("first", "second");
    }

    [Fact]
    public async Task AnElementLargerThanTheReadBufferIsFramedAcrossTheRefills()
    {
        // 200 000 bytes of value is more than three 64 KB buffers: the walk has to survive a refill that
        // stops in the middle of a string, in the middle of a number, and between two elements.
        var filler = new string('x', 200_000);
        var path = Write($$"""
            {"section":"notes","objects":[{"big":"{{filler}}"},{"after":1},{"big":"{{filler}}"}]}
            """);

        var lengths = new List<int>();
        var afters = new List<int>();
        await foreach (var element in JsonFile.ReadObjectsAsync<JsonElement>(path, "objects"))
        {
            if (element.TryGetProperty("big", out var big))
            {
                lengths.Add(big.GetString()!.Length);
            }

            if (element.TryGetProperty("after", out var after))
            {
                afters.Add(after.GetInt32());
            }
        }

        lengths.Should().Equal(200_000, 200_000);
        afters.Should().Equal(1);
    }

    [Theory]
    [InlineData(@"a "" quote")]
    [InlineData(@"a \ backslash")]
    [InlineData("a ] } and { inside")]
    [InlineData("unicode é 中 \u0041")]
    [InlineData("tab\tand newline\n")]
    public async Task StringsCarryingTheWalksOwnBytes_AreDeliveredExactlyAsWritten(string value)
    {
        var encoded = JsonSerializer.Serialize(value);
        var path = Write($$"""
            {"section":"notes","objects":[{"text":{{encoded}}}]}
            """);

        var texts = new List<string>();
        await foreach (var element in JsonFile.ReadObjectsAsync<JsonElement>(path, "objects"))
        {
            texts.Add(element.GetProperty("text").GetString()!);
        }

        texts.Should().Equal(value);
    }

    [Fact]
    public async Task AnEmptyArrayHandsOverNothing()
    {
        var path = Write("""{"section":"tables","objects":[]}""");

        var count = 0;
        await foreach (var _ in JsonFile.ReadObjectsAsync<JsonElement>(path, "objects"))
        {
            count++;
        }

        count.Should().Be(0);
    }

    [Fact]
    public async Task AFileWrittenByTheOlderWriters_StillReadsWithTheByteOrderMarkSteppedOver()
    {
        var path = Path.Combine(_dir, "withBom.json");
        File.WriteAllText(path, """{"section":"tables","objects":[{"n":"a"}]}""", new UTF8Encoding(true));

        JsonElement? first = null;
        await foreach (var element in JsonFile.ReadObjectsAsync<JsonElement>(path, "objects"))
        {
            first ??= element;
        }

        first!.Value.GetProperty("n").GetString().Should().Be("a");
    }

    [Fact]
    public async Task AFileThatStopsInTheMiddleOfAnElement_IsReportedByTheDeserializerNotGuessedAt()
    {
        // The framer's documented ending: it hands over the partial bytes and lets the parse answer for
        // them, because inventing a value is what a damaged catalog must not produce.
        var path = Write(@"{""section"":""tables"",""objects"":[{""n"":""untruncated");

        var act = async () =>
        {
            await foreach (var _ in JsonFile.ReadObjectsAsync<JsonElement>(path, "objects"))
            {
            }
        };

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task ADocumentTheFormatKeepsSmallIsDeserializedFromTheStreamNotFromAString()
    {
        // ReadDocumentAsync is the other half of R4: bounded documents (manifest, catalog.json,
        // table.json) come off the file's stream, whole but never as text.
        var path = Write("""{"a":1,"nested":{"b":"two"},"list":[1,2,3]}""");

        var document = await JsonFile.ReadDocumentAsync<JsonElement>(path, CancellationToken.None);

        document.GetProperty("a").GetInt32().Should().Be(1);
        document.GetProperty("nested").GetProperty("b").GetString().Should().Be("two");
        document.GetProperty("list").GetArrayLength().Should().Be(3);
    }

    private string Write(string json)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
