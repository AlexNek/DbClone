using System.Globalization;
using System.Text;

namespace DatabaseInterchange.Report;

/// <summary>
/// The report's text and number formatting, in one place so the same rule answers in every section.
/// </summary>
/// <remarks>
/// <para>
/// Both renderers format through these helpers because §7 asserts the escaping rule over the whole
/// document: a name that needs escaping in a register cell needs the same escape in a statistics cell,
/// and two implementations would drift. Every choice here is a determinism choice (D9): ordinal
/// comparisons, invariant-culture numbers, half-up rounding, and no locale-sensitive character anywhere.
/// </para>
/// <para>
/// Nothing in this class touches a filesystem, a database or a culture-specific default.
/// </para>
/// </remarks>
internal static class ReportText
{
    /// <summary>Gets the text a list with no entries renders as (FR10).</summary>
    public const string None = "none";

    /// <summary>Gets the placeholder a byte or row column shows when no data was captured (FR10).</summary>
    public const string NoData = "no data captured";

    /// <summary>Gets the marker for a referential action left at its default (§5.2).</summary>
    public const string NotStated = "-";

    /// <summary>
    /// Gets the single row the two catalog root files are reported under (FR5): neither can be listed
    /// in <c>manifest.json</c>, so the only figure the writer keeps for them is their summed length.
    /// </summary>
    public const string RootMetadataFiles = "manifest.json + export.json";

    /// <summary>Formats an exact byte count: a bare invariant number, so a value is greppable (D9.1).</summary>
    public static string Bytes(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a count invariantly, with no digit grouping (D9.1).</summary>
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats one human size on 1024 divisors with binary unit names, <c>F1</c> half-up (D9.1).
    /// </summary>
    /// <remarks>
    /// Prose and card headings only: every table cell carries the exact byte count, so a rounded
    /// restatement can never stand in for a measured fact.
    /// </remarks>
    public static string HumanSize(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        if (value < 1024)
        {
            return $"{value} {units[0]}";
        }

        var size = (decimal)value;
        var unit = 0;
        while (size >= 1024m && unit < units.Length - 1)
        {
            size /= 1024m;
            unit++;
        }

        var rounded = Math.Round(size, 1, MidpointRounding.AwayFromZero);

        // A value that rounds up to the next power keeps one decimal so the text never reads "1024.0 MiB".
        if (rounded == 1024.0m && unit < units.Length - 1)
        {
            return $"1.0 {units[unit + 1]}";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{rounded:F1} {units[unit]}");
    }

    /// <summary>
    /// Formats <paramref name="part"/> as a whole percentage of <paramref name="whole"/>, half-up,
    /// printed beside the raw counts it came from (D9.1).
    /// </summary>
    public static string Percent(long part, long whole)
    {
        if (whole <= 0)
        {
            return "0";
        }

        var percent = Math.Round((decimal)part * 100m / whole, 0, MidpointRounding.AwayFromZero);
        return ((long)percent).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Renders a qualified table name the way the catalog spells it: <c>schema.table</c>.</summary>
    public static string Qualified(string schema, string table) => $"{schema}.{table}";

    /// <summary>Wraps a name in an inline code span, doubling the fence when the name itself holds a backtick.</summary>
    public static string Code(string value)
    {
        // A code span cannot escape a backtick; a longer fence delimits it instead (§5.9).
        var fence = value.Contains('`', StringComparison.Ordinal) ? "``" : "`";
        var text = value;
        if (fence == "``" && text.StartsWith('`'))
        {
            text = " " + text;
        }

        if (fence == "``" && text.EndsWith('`'))
        {
            text += " ";
        }

        return $"{fence}{text}{fence}";
    }

    /// <summary>
    /// Renders a value for a Markdown table cell: plain when it is plain-safe, a code span when it
    /// carries a character the table syntax would read (§5.9).
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return NotStated;
        }

        return value.Any(c => c is '|' or '`' or '\r' or '\n') ? EscapeCell(Code(value)) : value;
    }

    /// <summary>Escapes the characters that would break a Markdown table row inside an already-built cell.</summary>
    public static string EscapeCell(string value) =>
        value.Replace("|", @"\|", StringComparison.Ordinal)
             .Replace("\r\n", " ", StringComparison.Ordinal)
             .Replace('\n', ' ')
             .Replace('\r', ' ');

    /// <summary>Joins column names for one cell, escaping each so a name cannot split the row (§5.9).</summary>
    public static string ColumnList(IReadOnlyList<string> columns) =>
        columns.Count == 0
            ? NotStated
            : string.Join(", ", columns.Select(Cell));

    /// <summary>Joins column names for one prose listing, escaping each so a name cannot split the row.</summary>
    public static string CodeList(IReadOnlyList<string> values) =>
        values.Count == 0
            ? NotStated
            : string.Join(", ", values.Select(Code));

    /// <summary>Joins already-rendered items with the prose separator used across §6.</summary>
    public static string Facts(params string[] items) =>
        string.Join(ReportBudget.FactSeparator, items.Where(i => !string.IsNullOrEmpty(i)));

    /// <summary>
    /// Lays out one GitHub-flavoured Markdown table: a header row, a separator row, then one row per
    /// entry. Both renderers build their tables here, so the escaping rule of §5.9 is one rule and the
    /// same name cannot render safely in a register and raw in a statistics table.
    /// </summary>
    /// <param name="headers">The column captions, in print order.</param>
    /// <param name="rows">
    /// The rows, each carrying exactly one already-rendered cell per header. A cell arriving pre-rendered
    /// is deliberate: escaping twice would print a stray backslash.
    /// </param>
    public static string Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Any(r => r.Count != headers.Count))
        {
            throw new ArgumentException("Every row must carry exactly one cell per header.", nameof(rows));
        }

        var builder = new StringBuilder();
        builder.Append("| ").Append(string.Join(" | ", headers)).Append(" |\n");
        builder.Append("| ").Append(string.Join(" | ", headers.Select(_ => "---"))).Append(" |\n");

        foreach (var row in rows)
        {
            builder.Append("| ").Append(string.Join(" | ", row)).Append(" |\n");
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Renders a referential action: the default is a dash, everything else is stated in lower case
    /// (§5.2). Both actions are printed because both change what a DELETE or an UPDATE does.
    /// </summary>
    public static string Action(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)
            || string.Equals(action, "NO ACTION", StringComparison.OrdinalIgnoreCase))
        {
            return NotStated;
        }

        return action.ToLower(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Splits a PascalCase enum name into the words an end user reads, e.g.
    /// <c>ConsistentSnapshot</c> → <c>consistent snapshot</c>.
    /// </summary>
    public static string Words(string pascalCase)
    {
        if (string.IsNullOrEmpty(pascalCase))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(pascalCase.Length + 4);
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(pascalCase[i - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>Renders a captured export time in UTC, or the stated degradation when there is none.</summary>
    public static string Timestamp(DateTimeOffset? exportedAt) =>
        exportedAt is null
            ? "export time unknown"
            : exportedAt.Value.ToUniversalTime().ToString(TimestampFormatText, CultureInfo.InvariantCulture);

    private const string TimestampFormatText = ReportBudget.TimestampFormat;
}
