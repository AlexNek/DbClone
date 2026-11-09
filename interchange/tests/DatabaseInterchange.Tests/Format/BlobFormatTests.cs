using System.Text;
using System.Text.RegularExpressions;

using DatabaseInterchange.Format;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Format;

/// <summary>
/// The invariants design §3.2 says the compiled naming table is held by: the suffix literals, the probe
/// budget, the ordering that keeps one value from being claimed twice, and the class rules that decide
/// what a name may be based on. Plus the fixtures §3.1 lists, because a table is only as good as the
/// near-misses it survives.
/// </summary>
public static class BlobFormatTests
{
    /// <summary>
    /// The bound of design §3.2 rule 4 as amended: the longest name in the table is <c>parquet</c>.
    /// </summary>
    private static readonly Regex SuffixRule = new("^[a-z0-9]{1,7}$", RegexOptions.Compiled);

    private static readonly string[] ReservedDeviceNames =
    [
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    ];

    [Fact]
    public static void EverySuffix_IsALowercaseLiteralWithinTheTablesOwnBound()
    {
        var offenders = BlobFormat.Formats
            .Select(e => e.Suffix)
            .Distinct()
            .Where(s => !SuffixRule.IsMatch(s) || ReservedDeviceNames.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();

        offenders.Should().BeEmpty(
            "a suffix is a reviewed literal inside the bound the naming table itself sets (§3.2 rule 4)");
    }

    [Fact]
    public static void NoMagic_IsLongerThanTheProbeBudget()
    {
        foreach (var entry in BlobFormat.Formats)
        {
            entry.Magic.Length.Should().BeLessThanOrEqualTo(BlobFormat.ProbeBytes,
                $"the {entry.Suffix} entry must be decidable inside the fixed probe budget");
        }
    }

    [Fact]
    public static void EverySharedMagic_LeavesItsLastEntryAsTheDeclaredFallback()
    {
        // §3.2 rule 1: no entry may be claimable by another except where the shallower entry is the
        // deeper one's declared fallback. Mechanically that means: inside a group of entries sharing one
        // magic, every entry that is not last must add its own header assertion — an entry with a magic
        // alone and nothing after it would swallow the values its siblings were written for.
        var groups = BlobFormat.Formats
            .Select((entry, index) => (entry, index))
            .GroupBy(x => Convert.ToHexString(x.entry.Magic));

        foreach (var group in groups.Where(g => g.Count() > 1))
        {
            var ordered = group.OrderBy(x => x.index).ToList();
            for (var i = 0; i < ordered.Count - 1; i++)
            {
                ordered[i].entry.CheckFunc.Should().NotBeNull(
                    $"'{ordered[i].entry.Suffix}' shares its magic with the later '{ordered[^1].entry.Suffix}' " +
                    "and must therefore assert more than the magic");
            }
        }
    }

    [Fact]
    public static void NoEntryShadowsALongerSignaturePlacedAfterIt()
    {
        for (var i = 0; i < BlobFormat.Formats.Length; i++)
        {
            for (var j = i + 1; j < BlobFormat.Formats.Length; j++)
            {
                var earlier = BlobFormat.Formats[i].Magic;
                var later = BlobFormat.Formats[j].Magic;
                if (later.Length > earlier.Length && IsPrefix(earlier, later) && earlier.Length > 0)
                {
                    Assert.Fail(
                        $"'{BlobFormat.Formats[i].Suffix}' matches on a prefix of the magic " +
                        $"'{BlobFormat.Formats[j].Suffix}' needs, so the longer signature is unreachable (§3.2 rule 1)");
                }
            }
        }
    }

    [Fact]
    public static void TextStartingLikeBitmap_IsNotNamedBmp()
    {
        // "BM" is the first two bytes of the English word "Bitmap", and the class rule refuses it.
        Detect("Bitmap image, not a header at all, padded out past fourteen bytes.")
            .Should().Be(BlobFormat.Fallback);
    }

    [Fact]
    public static void ZeroPaddedLittleEndianWord_IsNotNamedIco()
    {
        Detect([0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])
            .Should().Be(BlobFormat.Fallback, "an ICO directory has to name at least one image");
    }

    [Fact]
    public static void TrueTypeVersionWord_IsNotNamedTtf()
    {
        Detect([0x00, 0x01, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00])
            .Should().Be(BlobFormat.Fallback);
    }

    [Fact]
    public static void ArbitraryBytesStartingLikeAnMp3Frame_AreNotNamedMp3()
    {
        Detect([0xFF, 0xF3, 0x10, 0x43, 0x00, 0x00])
            .Should().Be(BlobFormat.Fallback);
    }

    [Fact]
    public static void GzipMagicWithTheWrongCompressionMethod_IsNotNamedGz()
    {
        Detect([0x1F, 0x8B, 0x00, 0x00, 0x00, 0x00])
            .Should().Be(BlobFormat.Fallback, "CM must be 8 (§3.1); 0 is a header that cannot be gzip");
    }

    [Fact]
    public static void JsonAndCsvPayloads_AreNamedNeither()
    {
        Detect("""{"items": [1, 2, 3]}""").Should().Be(BlobFormat.Fallback);
        Detect("id,name\n1,test_user\n").Should().Be(BlobFormat.Fallback);
    }

    [Fact]
    public static void LegacyOffice_Payload_IsNamedForItsContainerOnly()
    {
        // §9: a CBFF name would need a FAT walk to know whether it holds a doc, an xls or a ppt.
        Detect([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00])
            .Should().Be("cfb");
    }

    [Fact]
    public static void SvgPayload_IsNamedSvg_WhilePlainXmlStaysXml()
    {
        Detect("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 8 8"><rect/></svg>""")
            .Should().Be("svg");
        Detect("""<?xml version="1.0"?><note><to>test</to></note>""")
            .Should().Be("xml");
    }

    [Fact]
    public static void OoxmlPackage_IsNamedFromItsPartDirectory_NotFromTheFirstEntry()
    {
        // A real package opens with [Content_Types].xml and carries the kind's part later in the chain,
        // so naming one means walking headers rather than reading the first name and giving up (§3.1).
        var package = ZipPackage(
            ("[Content_Types].xml", "<Types/>"),
            ("word/document.xml", "<w:document/>"));

        Detect(package).Should().Be("docx");
    }

    [Fact]
    public static void OoxmlPackage_WithItsPartPastTheProbeBudget_DegradesToTheContainerName()
    {
        // The budget decides the name, never luck: push the part the check looks for past 512 bytes and
        // the value keeps the truthful container name instead of waiting for a larger read.
        var package = ZipPackage(
            ("[Content_Types].xml", new string('x', 600)),
            ("word/document.xml", "<w:document/>"));

        Detect(package).Should().Be("zip");
    }

    [Fact]
    public static void SpreadsheetPackage_IsNamedXlsx_AndOdfFromItsMimetype()
    {
        Detect(ZipPackage(("[Content_Types].xml", "<Types/>"), ("xl/workbook.xml", "<workbook/>")))
            .Should().Be("xlsx");
        Detect(OdfPackage("application/vnd.oasis.opendocument.spreadsheet"))
            .Should().Be("ods");
    }

    [Fact]
    public static void EpubPackage_IsNamedFromItsMimetype_NotFromTheArchiveShape()
    {
        // §3.1 lists epub among the ZIP-family names the probe has to reach, so the same header walk that
        // names an ODS has to name this one — an archive that stops at `zip` here is a probe that gave up.
        Detect(OdfPackage("application/epub+zip")).Should().Be("epub");
    }

    [Fact]
    public static void ZipWhoseEntriesBelongToNothing_KnownIsStillZip()
    {
        Detect(ZipPackage(("readme.txt", "stored for the test")))
            .Should().Be("zip");
    }

    [Fact]
    public static void EveryCheck_IsDecidableInsideTheBudgetWithoutReadingPastIt()
    {
        // §3.2 rule 3: the budget promise is what keeps naming a constant cost, so it is pinned for every
        // assertion the table declares, not only for the magic prefixes. A probe of exactly the budget is
        // the smallest buffer a check may be handed — one that reaches past it throws here.
        var probe = new byte[BlobFormat.ProbeBytes];

        foreach (var entry in BlobFormat.Formats.Where(e => e.CheckFunc is not null))
        {
            var check = entry.CheckFunc!;
            var act = () => check(probe, probe.Length);

            act.Should().NotThrow(
                $"'{entry.Suffix}' asserts on a probe the budget bounds, so it may not read outside it");
        }
    }

    [Fact]
    public static void Detection_CostsTheSameOnAValueFarLargerThanTheBudget()
    {
        // The promise of a fixed budget: a short PNG and a long one are named from the same number of
        // bytes, so naming never becomes a function of the value's size.
        var shortValue = new byte[64];
        var longValue = new byte[4096];
        PngHeader(shortValue.AsSpan());
        PngHeader(longValue.AsSpan());

        var fromShort = BlobFormat.DetectSuffix(shortValue, Math.Min(shortValue.Length, BlobFormat.ProbeBytes));
        var fromLong = BlobFormat.DetectSuffix(longValue, Math.Min(longValue.Length, BlobFormat.ProbeBytes));

        fromShort.Should().Be("png");
        fromLong.Should().Be(fromShort);
    }

    private static string Detect(string text) =>
        Detect(Encoding.UTF8.GetBytes(text));

    private static string Detect(ReadOnlySpan<byte> bytes)
    {
        var probe = bytes.Length > BlobFormat.ProbeBytes
            ? bytes[..BlobFormat.ProbeBytes].ToArray()
            : bytes.ToArray();

        return BlobFormat.DetectSuffix(probe, probe.Length);
    }

    private static void PngHeader(Span<byte> target)
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(target);
    }

    /// <summary>
    /// Builds stored (uncompressed) ZIP local headers, the only part of the container a probe can see.
    /// </summary>
    private static byte[] ZipPackage(params (string Name, string Content)[] entries)
    {
        var bytes = new List<byte>();
        foreach (var (name, content) in entries)
        {
            AppendLocalEntry(bytes, name, Encoding.ASCII.GetBytes(content));
        }

        return [.. bytes];
    }

    private static byte[] OdfPackage(string mimetype)
    {
        var bytes = new List<byte>();
        AppendLocalEntry(bytes, "mimetype", Encoding.ASCII.GetBytes(mimetype), stored: true);
        AppendLocalEntry(bytes, "content.xml", Encoding.ASCII.GetBytes("<document/>"));

        return [.. bytes];
    }

    private static void AppendLocalEntry(
        List<byte> bytes,
        string name,
        byte[] data,
        bool stored = false)
    {
        bytes.AddRange([0x50, 0x4B, 0x03, 0x04]);
        WriteUInt16(bytes, 20);                       // version needed to extract
        WriteUInt16(bytes, 0);                        // general flags
        WriteUInt16(bytes, (ushort)(stored ? 0 : 8)); // method: stored or deflated
        WriteUInt16(bytes, 0);                        // modified time
        WriteUInt16(bytes, 0);                        // modified date
        WriteUInt32(bytes, 0);                        // crc — unread by a probe
        WriteUInt32(bytes, (uint)data.Length);        // compressed size
        WriteUInt32(bytes, (uint)data.Length);        // uncompressed size
        WriteUInt16(bytes, (ushort)Encoding.ASCII.GetByteCount(name));
        WriteUInt16(bytes, 0);                        // extra field length
        bytes.AddRange(Encoding.ASCII.GetBytes(name));
        bytes.AddRange(data);
    }

    private static void WriteUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
    }

    private static void WriteUInt32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 24));
    }

    private static bool IsPrefix(byte[] candidate, byte[] longer)
    {
        if (candidate.Length > longer.Length)
        {
            return false;
        }

        for (var i = 0; i < candidate.Length; i++)
        {
            if (candidate[i] != longer[i])
            {
                return false;
            }
        }

        return true;
    }
}
