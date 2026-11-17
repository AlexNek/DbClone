using System.Text;

namespace DatabaseInterchange.Format;

/// <summary>
/// Validation functions for format detection. Each check asserts header internals within
/// the 512-byte probe budget (<see cref="BlobFormat.ProbeBytes"/>); a unit test pins that promise, so
/// naming a value stays a constant cost however large the value is.
/// </summary>
internal static class FormatChecks
{
    // === JPEG: APPn or SOF marker after FF D8 FF ===
    internal static bool JpegMarker(byte[] probe, int length)
    {
        if (length < 4) return false;
        var marker = probe[3];
        return marker is >= 0xC0 and <= 0xEF;
    }

    // === BMP: reserved zero fields + pixel data offset >= 54 ===
    internal static bool BmpHeader(byte[] probe, int length)
    {
        if (length < 14) return false;
        if (probe[2] != 0 || probe[3] != 0 || probe[4] != 0 || probe[5] != 0) return false;
        var dataOffset = BitConverter.ToInt32(probe, 10);
        return dataOffset >= 54;
    }

    // === ICO: type in {1, 2} and count > 0 ===
    internal static bool IcoDir(byte[] probe, int length)
    {
        if (length < 6) return false;
        var type = BitConverter.ToUInt16(probe, 2);
        if (type != 1 && type != 2) return false;
        var count = BitConverter.ToUInt16(probe, 4);
        return count > 0;
    }

    // === RIFF with WEBP fourcc ===
    internal static bool RiffWebp(byte[] probe, int length)
    {
        if (length < 12) return false;
        return probe[8] == 0x57 && probe[9] == 0x45 && probe[10] == 0x42 && probe[11] == 0x50;
    }

    // === RIFF with WAVE fourcc ===
    internal static bool RiffWav(byte[] probe, int length)
    {
        if (length < 12) return false;
        return probe[8] == 0x57 && probe[9] == 0x41 && probe[10] == 0x56 && probe[11] == 0x45;
    }

    // === RIFF with AVI fourcc ===
    internal static bool RiffAvi(byte[] probe, int length)
    {
        if (length < 12) return false;
        return probe[8] == 0x41 && probe[9] == 0x56 && probe[10] == 0x49 && probe[11] == 0x20;
    }

    // === FORM with AIFF fourcc ===
    internal static bool FormAiff(byte[] probe, int length)
    {
        if (length < 12) return false;
        return probe[8] == 0x41 && probe[9] == 0x49 && probe[10] == 0x46 && probe[11] == 0x46;
    }

    // === ISO-BMFF: ftyp brand for HEIC ===
    internal static bool IsoBmftFtypHeic(byte[] probe, int length)
    {
        if (length < 12) return false;
        if (!IsFtyp(probe)) return false;
        var brand = Encoding.ASCII.GetString(probe, 8, 4);
        return brand is "heic" or "heix" or "hevc" or "hevx";
    }

    // === ISO-BMFF: ftyp brand for AVIF ===
    internal static bool IsoBmftFtypAvif(byte[] probe, int length)
    {
        if (length < 12) return false;
        if (!IsFtyp(probe)) return false;
        return probe[8] == 0x61 && probe[9] == 0x76 && probe[10] == 0x69 && probe[11] == 0x66;
    }

    // === ISO-BMFF: ftyp brand for M4A ===
    internal static bool IsoBmftFtypM4a(byte[] probe, int length)
    {
        if (length < 12) return false;
        if (!IsFtyp(probe)) return false;
        return probe[8] == 0x4D && probe[9] == 0x34 && probe[10] == 0x41 && probe[11] == 0x20;
    }

    // === ISO-BMFF: ftyp brand for MP4 ===
    internal static bool IsoBmftFtypMp4(byte[] probe, int length)
    {
        if (length < 12) return false;
        if (!IsFtyp(probe)) return false;
        var brand = Encoding.ASCII.GetString(probe, 8, 4);
        return brand is "isom" or "mp41" or "mp42" or "iso5" or "iso6";
    }

    // === ISO-BMFF: ftyp brand for MOV ===
    internal static bool IsoBmftFtypMov(byte[] probe, int length)
    {
        if (length < 12) return false;
        if (!IsFtyp(probe)) return false;
        return probe[8] == 0x71 && probe[9] == 0x74 && probe[10] == 0x20 && probe[11] == 0x20;
    }

    private static bool IsFtyp(byte[] probe) =>
        probe[4] == 0x66 && probe[5] == 0x74 && probe[6] == 0x79 && probe[7] == 0x70;

    // === EBML/WebM: DocType string within probe ===
    internal static bool EbmlWebm(byte[] probe, int length)
    {
        var searchLen = Math.Min(length, BlobFormat.ProbeBytes);
        ReadOnlySpan<byte> webm = "webm"u8;
        for (var i = 4; i <= searchLen - webm.Length; i++)
        {
            if (probe.AsSpan(i, webm.Length).SequenceEqual(webm))
                return true;
        }
        return false;
    }

    // === gzip: CM = 8 at offset 2 ===
    internal static bool GzipCm(byte[] probe, int length)
    {
        if (length < 3) return false;
        return probe[2] == 0x08;
    }

    // === bzip2: block-size digit at offset 2 ===
    internal static bool Bzip2Digit(byte[] probe, int length)
    {
        if (length < 3) return false;
        return probe[2] >= 0x31 && probe[2] <= 0x39; // '1'-'9'
    }

    // === tar: "ustar" at offset 257 ===
    internal static bool TarUstar(byte[] probe, int length)
    {
        if (length < 262) return false;
        return probe[257] == 0x75 && probe[258] == 0x73 && probe[259] == 0x74
            && probe[260] == 0x61 && probe[261] == 0x72;
    }

    // === PE: MZ + valid PE header offset ===
    internal static bool PeHeader(byte[] probe, int length)
    {
        if (length < 64) return false;
        var peOffset = BitConverter.ToInt32(probe, 60);
        if (peOffset < 0 || peOffset + 4 > length) return false;
        return probe[peOffset] == 0x50 && probe[peOffset + 1] == 0x45
            && probe[peOffset + 2] == 0x00 && probe[peOffset + 3] == 0x00;
    }

    // === ZIP local header validation ===
    internal static bool ZipLocalHeader(byte[] probe, int length)
    {
        if (length < 30) return false;
        var version = BitConverter.ToUInt16(probe, 4);
        return version <= 63;
    }

    // === OOXML: the header chain carries the kind's directory entry ===
    internal static bool OoxmlWord(byte[] probe, int length) =>
        OoxmlChainContains(probe, length, "word/");

    internal static bool OoxmlSpreadsheet(byte[] probe, int length) =>
        OoxmlChainContains(probe, length, "xl/");

    internal static bool OoxmlPresentation(byte[] probe, int length) =>
        OoxmlChainContains(probe, length, "ppt/");

    // === ODF mimetype checks ===
    internal static bool OdtMimetype(byte[] probe, int length) =>
        ZipContainsMimetype(probe, length, "application/vnd.oasis.opendocument.text");

    internal static bool OdsMimetype(byte[] probe, int length) =>
        ZipContainsMimetype(probe, length, "application/vnd.oasis.opendocument.spreadsheet");

    internal static bool OdpMimetype(byte[] probe, int length) =>
        ZipContainsMimetype(probe, length, "application/vnd.oasis.opendocument.presentation");

    internal static bool EpubMimetype(byte[] probe, int length) =>
        ZipContainsMimetype(probe, length, "application/epub+zip");

    // === SVG: the root element is <svg>, with or without an XML declaration in front of it ===
    internal static bool SvgRoot(byte[] probe, int length)
    {
        var span = probe.AsSpan(0, Math.Min(length, BlobFormat.ProbeBytes));
        if (span.StartsWith("<svg"u8)) return true;
        if (!span.StartsWith("<?xml"u8)) return false;

        // An XML payload is only an SVG when its own root element says so; the declaration alone proves
        // nothing about the vocabulary, so a plain XML document keeps its `xml` name.
        return ContainsTag(span, "<svg"u8);
    }

    /// <summary>
    /// Walks the ZIP local-file-header chain and asserts that the package opens with
    /// <c>[Content_Types].xml</c> and that a reachable entry carries the Office kind's directory prefix.
    /// </summary>
    /// <remarks>
    /// A real Office package puts <c>[Content_Types].xml</c> first and the <c>word/</c> / <c>xl/</c> /
    /// <c>ppt/</c> part later in the chain, so naming one means walking headers rather than reading the
    /// first name and giving up. The walk is bounded by the probe budget: when the chain runs past it, the
    /// value keeps the truthful container name <c>zip</c>, which is what §3.1 asks for — a budget decides
    /// the name, never luck.
    /// </remarks>
    private static bool OoxmlChainContains(byte[] probe, int length, string partPrefix)
    {
        var offset = 0;
        var expectContentTypes = true;

        while (offset + 30 <= length)
        {
            if (!IsLocalFileHeader(probe, offset)) return false;

            var compressedSize = BitConverter.ToInt32(probe, offset + 18);
            var nameLength = BitConverter.ToUInt16(probe, offset + 26);
            var extraLength = BitConverter.ToUInt16(probe, offset + 28);
            var nameStart = offset + 30;
            if (nameStart + nameLength > length || compressedSize < 0) return false;

            var name = Encoding.ASCII.GetString(probe, nameStart, nameLength);
            if (expectContentTypes)
            {
                if (name != "[Content_Types].xml") return false;
                expectContentTypes = false;
            }
            else if (name.StartsWith(partPrefix, StringComparison.Ordinal))
            {
                return true;
            }

            // Zero sizes mean the real ones live in a data descriptor that follows the data, so the next
            // header is not reachable from the bytes at hand.
            if (compressedSize == 0) return false;
            offset = nameStart + nameLength + extraLength + compressedSize;
        }

        return false;
    }

    private static bool IsLocalFileHeader(byte[] probe, int offset) =>
        probe[offset] == 0x50 && probe[offset + 1] == 0x4B
        && probe[offset + 2] == 0x03 && probe[offset + 3] == 0x04;

    /// <summary>
    /// Checks whether the ZIP's first entry is "mimetype" (stored uncompressed) and contains
    /// the expected MIME string.
    /// </summary>
    private static bool ZipContainsMimetype(byte[] probe, int length, string mimetype)
    {
        if (length < 30) return false;
        var filenameLen = BitConverter.ToUInt16(probe, 26);
        if (filenameLen != 8) return false;
        if (30 + filenameLen > length) return false;
        var filename = Encoding.ASCII.GetString(probe, 30, 8);
        if (filename != "mimetype") return false;

        // Compression method at offset 8 — must be 0 (stored)
        var compressionMethod = BitConverter.ToUInt16(probe, 8);
        if (compressionMethod != 0) return false;

        var compressedSize = BitConverter.ToInt32(probe, 18);
        var uncompressedSize = BitConverter.ToInt32(probe, 22);
        if (compressedSize != uncompressedSize) return false;

        var extraLen = BitConverter.ToUInt16(probe, 28);
        var dataOffset = 30 + filenameLen + extraLen;
        if (dataOffset + compressedSize > length) return false;

        var content = Encoding.ASCII.GetString(probe, dataOffset, compressedSize);
        return content == mimetype;
    }

    private static bool ContainsTag(ReadOnlySpan<byte> span, ReadOnlySpan<byte> tag)
    {
        for (var i = 0; i <= span.Length - tag.Length; i++)
        {
            if (span.Slice(i, tag.Length).SequenceEqual(tag)) return true;
        }

        return false;
    }
}
