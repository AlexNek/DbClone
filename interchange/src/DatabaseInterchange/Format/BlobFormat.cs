namespace DatabaseInterchange.Format;

/// <summary>
/// Detects the format of binary data from its magic bytes. The naming table is code, not
/// configuration — a magic number is a line in a public file-format specification, not a
/// per-deployment or per-dialect fact.
/// </summary>
/// <remarks>
/// <para>The table is ordered longest/most-specific first. Detection uses a 512-byte probe
/// budget. Every check's deepest read is within that budget.</para>
/// <para>Format detection is from the bytes themselves, never from a column value. Measured on
/// real data, declared content types contradict the bytes in ~50% of cases.</para>
/// </remarks>
internal static class BlobFormat
{
    /// <summary>The number of bytes probed for format detection. A compile-time constant.</summary>
    internal const int ProbeBytes = 512;

    /// <summary>The suffix for unrecognized formats — always "bin".</summary>
    internal const string Fallback = "bin";

    /// <summary>
    /// Detects the format suffix from the first bytes of a value. Returns the lowercase suffix
    /// (e.g. "jpg", "png") or "bin" when the format is unrecognized.
    /// </summary>
    /// <param name="probe">The first bytes of the value (up to <see cref="ProbeBytes"/>).</param>
    /// <param name="length">The number of valid bytes in <paramref name="probe"/>.</param>
    /// <returns>The detected format suffix.</returns>
    internal static string DetectSuffix(byte[] probe, int length)
    {
        foreach (var entry in Formats)
        {
            if (entry.Matches(probe, length))
            {
                return entry.Suffix;
            }
        }

        return Fallback;
    }

    /// <summary>
    /// Asserts that a stored container name is still true of the bytes it points at (design §6.8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not re-detection: the reader does not pick the most specific entry the bytes deserve, it
    /// asks whether the name already on the file is one the bytes carry. A name that no entry of this
    /// suffix can claim means the container or the row file changed after it was written — the writer
    /// never renames or re-encodes, so nothing else could have moved the bytes.
    /// </para>
    /// <para>
    /// <see cref="Fallback"/> claims no signature, so it is always true of its bytes: an unnamed value is
    /// stored, referenced, verified and restored exactly like a named one (§6.9).
    /// </para>
    /// </remarks>
    /// <param name="suffix">The suffix the stored name carries.</param>
    /// <param name="probe">The first bytes at the referenced range.</param>
    /// <param name="length">The number of valid bytes in <paramref name="probe"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the bytes carry the signature of <paramref name="suffix"/>, or when the
    /// suffix is <see cref="Fallback"/>; <see langword="false"/> when the name contradicts the bytes or
    /// names nothing this table knows.
    /// </returns>
    internal static bool NameMatchesBytes(string suffix, byte[] probe, int length)
    {
        if (suffix == Fallback)
        {
            return true;
        }

        foreach (var entry in Formats)
        {
            if (string.Equals(entry.Suffix, suffix, StringComparison.Ordinal) && entry.Matches(probe, length))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One entry in the naming table. Ordered longest/most-specific first.
    /// </summary>
    /// <remarks>
    /// Internal rather than private because design §3.2 says the table's invariants are held by tests, and a test
    /// cannot assert over a list it cannot read. Nothing outside this assembly uses it.
    /// </remarks>
    internal readonly record struct FormatEntry(
        string Suffix,
        FormatClass DetectionClass,
        byte[] Magic,
        FormatCheck? CheckFunc = null)
    {
        public bool Matches(byte[] probe, int length)
        {
            // Check function only (no magic prefix)
            if (Magic.Length == 0)
            {
                return CheckFunc is not null && CheckFunc(probe, length);
            }

            // Magic must fit within the probe
            if (length < Magic.Length) return false;

            for (var i = 0; i < Magic.Length; i++)
            {
                if (probe[i] != Magic[i]) return false;
            }

            // For Exact class, magic match is sufficient
            if (DetectionClass == FormatClass.Exact) return true;

            // For Validated and Probe, run the check function if present
            if (CheckFunc is not null)
            {
                return CheckFunc(probe, length);
            }

            return true;
        }
    }

    /// <summary>
    /// The ordered naming table. Longest/most-specific first. A test asserts pairwise
    /// non-ambiguity: no entry's magic may be claimable by another except where the shallower
    /// entry is the deeper one's declared fallback (e.g. zip after ods/xlsx).
    /// </summary>
    internal static readonly FormatEntry[] Formats =
    [
        // === Images ===
        new("png", FormatClass.Exact, [(byte)0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        new("gif", FormatClass.Exact, [0x47, 0x49, 0x46, 0x38, 0x39, 0x61]), // GIF89a
        new("gif", FormatClass.Exact, [0x47, 0x49, 0x46, 0x38, 0x37, 0x61]), // GIF87a
        new("webp", FormatClass.Validated, [0x52, 0x49, 0x46, 0x46], FormatChecks.RiffWebp),
        new("heic", FormatClass.Validated, [], FormatChecks.IsoBmftFtypHeic),
        new("avif", FormatClass.Validated, [], FormatChecks.IsoBmftFtypAvif),
        new("jpg", FormatClass.Validated, [0xFF, 0xD8, 0xFF], FormatChecks.JpegMarker),
        new("bmp", FormatClass.Validated, [0x42, 0x4D], FormatChecks.BmpHeader),
        new("ico", FormatClass.Validated, [0x00, 0x00, 0x01, 0x00], FormatChecks.IcoDir),
        new("tif", FormatClass.Exact, [0x49, 0x49, 0x2A, 0x00]), // little-endian TIFF
        new("tif", FormatClass.Exact, [0x4D, 0x4D, 0x00, 0x2A]), // big-endian TIFF
        new("psd", FormatClass.Exact, [0x38, 0x42, 0x50, 0x53]), // "8BPS"
        // Before "xml": an SVG payload is XML whose root element says "svg", and the root element is the
        // definitive marker the design's images row asks for (§3.1).
        new("svg", FormatClass.Validated, [], FormatChecks.SvgRoot),

        // === Documents ===
        new("pdf", FormatClass.Exact, [0x25, 0x50, 0x44, 0x46, 0x2D]), // "%PDF-"
        new("rtf", FormatClass.Exact, [0x7B, 0x5C, 0x72, 0x74, 0x66]), // "{\rtf"
        new("cfb", FormatClass.Exact, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]),

        // === OOXML (probe: ZIP header chain) ===
        new("docx", FormatClass.Probe, [0x50, 0x4B, 0x03, 0x04], FormatChecks.OoxmlWord),
        new("xlsx", FormatClass.Probe, [0x50, 0x4B, 0x03, 0x04], FormatChecks.OoxmlSpreadsheet),
        new("pptx", FormatClass.Probe, [0x50, 0x4B, 0x03, 0x04], FormatChecks.OoxmlPresentation),

        // === ODF (probe: ZIP + mimetype entry) ===
        new("odt", FormatClass.Probe, [0x50, 0x4B, 0x03, 0x04], FormatChecks.OdtMimetype),
        new("ods", FormatClass.Probe, [0x50, 0x4B, 0x03, 0x04], FormatChecks.OdsMimetype),
        new("odp", FormatClass.Probe, [0x50, 0x4B, 0x03, 0x04], FormatChecks.OdpMimetype),

        // === EPUB (probe: ZIP + mimetype = application/epub+zip) ===
        new("epub", FormatClass.Probe, [0x50, 0x4B, 0x03, 0x04], FormatChecks.EpubMimetype),

        // === ZIP (fallback for OOXML/ODF/EPUB probes that fail) ===
        new("zip", FormatClass.Validated, [0x50, 0x4B, 0x03, 0x04], FormatChecks.ZipLocalHeader),

        // === Archives ===
        new("gz", FormatClass.Validated, [0x1F, 0x8B], FormatChecks.GzipCm),
        new("bz2", FormatClass.Validated, [0x42, 0x5A, 0x68], FormatChecks.Bzip2Digit),
        new("xz", FormatClass.Exact, [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]),
        new("zst", FormatClass.Exact, [0x28, 0xB5, 0x2F, 0xFD]),
        new("7z", FormatClass.Exact, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]),
        new("rar", FormatClass.Exact, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07]),
        new("tar", FormatClass.Probe, [], FormatChecks.TarUstar),

        // === Audio ===
        new("flac", FormatClass.Exact, [0x66, 0x4C, 0x61, 0x43]), // "fLaC"
        new("ogg", FormatClass.Exact, [0x4F, 0x67, 0x67, 0x53]), // "OggS"
        new("wav", FormatClass.Validated, [0x52, 0x49, 0x46, 0x46], FormatChecks.RiffWav),
        new("aiff", FormatClass.Validated, [0x46, 0x4F, 0x52, 0x4D], FormatChecks.FormAiff),
        new("m4a", FormatClass.Validated, [], FormatChecks.IsoBmftFtypM4a),
        new("mp3", FormatClass.Exact, [0x49, 0x44, 0x33]), // "ID3"

        // === Video ===
        new("webm", FormatClass.Probe, [0x1A, 0x45, 0xDF, 0xA3], FormatChecks.EbmlWebm),
        new("mkv", FormatClass.Exact, [0x1A, 0x45, 0xDF, 0xA3]), // EBML (after webm)
        new("mp4", FormatClass.Validated, [], FormatChecks.IsoBmftFtypMp4),
        new("mov", FormatClass.Validated, [], FormatChecks.IsoBmftFtypMov),
        new("avi", FormatClass.Validated, [0x52, 0x49, 0x46, 0x46], FormatChecks.RiffAvi),

        // === Fonts ===
        new("woff", FormatClass.Exact, [0x77, 0x4F, 0x46, 0x46]), // "wOFF"
        new("woff2", FormatClass.Exact, [0x77, 0x4F, 0x46, 0x32]), // "wOF2"
        new("otf", FormatClass.Exact, [0x4F, 0x54, 0x54, 0x4F]), // "OTTO"

        // === Structured text ===
        new("xml", FormatClass.Exact, [0x3C, 0x3F, 0x78, 0x6D, 0x6C]), // "<?xml"
        new("txt", FormatClass.Exact, [0xEF, 0xBB, 0xBF]), // UTF-8 BOM
        new("txt", FormatClass.Exact, [0xFF, 0xFE]), // UTF-16 LE BOM
        new("txt", FormatClass.Exact, [0xFE, 0xFF]), // UTF-16 BE BOM

        // === Data / executables ===
        new("sqlite", FormatClass.Exact, [0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66,
            0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x33, 0x00]), // "SQLite format 3\0"
        new("parquet", FormatClass.Exact, [0x50, 0x41, 0x52, 0x31]), // "PAR1"
        new("class", FormatClass.Exact, [0xCA, 0xFE, 0xBA, 0xBE]), // Java class
        new("elf", FormatClass.Exact, [0x7F, 0x45, 0x4C, 0x46]), // "\x7FELF"
        new("pe", FormatClass.Validated, [0x4D, 0x5A], FormatChecks.PeHeader), // "MZ" + PE offset
    ];
}
