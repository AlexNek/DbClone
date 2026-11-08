using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DatabaseInterchange.Format;

/// <summary>
/// Closed five-rule algorithm that turns an object's qualified name into a legal, collision-free
/// filesystem path segment. The encoder runs at write time only; the result is recorded in
/// <c>catalog.json</c> and never re-derived by a reader.
/// </summary>
/// <remarks>
/// <para>Rules (applied in order):</para>
/// <list type="number">
/// <item>Percent-encode the unsafe set: <c>%</c> → <c>%25</c>, <c>~</c> → <c>%7E</c>, then every
/// character illegal on Windows/macOS/Linux (<c>\ / : * ? " &lt; &gt; |</c>, U+0000–U+001F,
/// leading/trailing <c>.</c> or space).</item>
/// <item>Reserved device names: if the encoded name is, case-insensitively and ignoring any
/// extension, one of <c>CON PRN AUX NUL COM1-9 LPT1-9 CLOCK$</c>, prefix with <c>~</c>.</item>
/// <item>Case/normalization collisions: group by <c>NFC(name).CaseFold()</c>; the ordinal-first
/// member keeps the plain name; others append <c>-&lt;8 hex of SHA-256 of the canonical
/// reference&gt;</c>.</item>
/// <item>Path budget: full path must stay within 240 characters. If not, truncate schema to 60
/// and table to 100 code points and append the discriminator suffix.</item>
/// <item>Last resort: if nothing works, put the table under <c>tables/_unmapped/&lt;64-hex&gt;/</c>
/// and raise an <c>UnmappableTableName</c> issue.</item>
/// </list>
/// </remarks>
public static class NameEncoder
{
    /// <summary>The maximum total path length from catalog root to a data file.</summary>
    public const int MaxPathBudget = 240;

    /// <summary>Truncated schema segment length when the path budget is exceeded.</summary>
    public const int TruncatedSchemaLength = 60;

    /// <summary>Truncated table segment length when the path budget is exceeded.</summary>
    public const int TruncatedTableLength = 100;

    /// <summary>The folder name for tables that cannot be encoded.</summary>
    public const string UnmappedFolder = "_unmapped";

    /// <summary>
    /// Encodes a single name segment (schema or table name) using rules 1–2 of the algorithm.
    /// Does not handle collision disambiguation (rule 3) — that requires the full set of siblings.
    /// </summary>
    /// <param name="name">The raw name from the database.</param>
    /// <returns>The encoded segment safe for use as a directory name.</returns>
    public static string EncodeSegment(string name)
    {
        var encoded = PercentEncodeUnsafe(name);
        encoded = PrefixReservedDevice(encoded);
        return encoded;
    }

    /// <summary>
    /// Resolves case-fold/normalization collisions within a set of sibling names, applying rule 3.
    /// Returns the encoded name for each input, with discriminator suffixes where needed.
    /// </summary>
    /// <param name="names">The raw sibling names (e.g. all table names within one schema).</param>
    /// <param name="getRef">
    /// A function that returns the canonical <see cref="CanonicalModel.Interchange.ObjectRef"/> for
    /// a raw name, used as the discriminator hash input.
    /// </param>
    /// <returns>
    /// A dictionary mapping each raw name to its encoded, possibly suffixed, directory name.
    /// </returns>
    public static IReadOnlyDictionary<string, string> ResolveCollisions(
        IReadOnlyList<string> names,
        Func<string, CanonicalModel.Interchange.ObjectRef> getRef)
    {
        // Step 1: encode each name.
        var encoded = new Dictionary<string, string>(names.Count);
        foreach (var name in names)
        {
            encoded[name] = EncodeSegment(name);
        }

        // Step 2: group by NFC(name).CaseFold() to find collisions.
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var key = NFC(name).ToLowerInvariant();
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }
            list.Add(name);
        }

        // Step 3: within each collision group, the ordinal-first member keeps the plain name;
        // others get a discriminator suffix.
        var result = new Dictionary<string, string>(names.Count);
        foreach (var group in groups.Values)
        {
            if (group.Count == 1)
            {
                result[group[0]] = encoded[group[0]];
                continue;
            }

            // Sort ordinally to find the winner.
            group.Sort(StringComparer.Ordinal);

            // Winner keeps the plain encoded name.
            result[group[0]] = encoded[group[0]];

            // Losers get the discriminator suffix.
            for (var i = 1; i < group.Count; i++)
            {
                var name = group[i];
                var refValue = getRef(name);
                var discriminator = refValue.DiscriminatorHex();
                result[name] = encoded[name] + "-" + discriminator;
            }
        }

        return result;
    }

    /// <summary>
    /// Applies the path budget rule (rule 4). If the full path exceeds the budget, truncates the
    /// schema and table segments and adds a discriminator suffix.
    /// </summary>
    /// <param name="encodedSchema">The encoded schema segment.</param>
    /// <param name="encodedTable">The encoded table segment.</param>
    /// <param name="catalogRootLength">The length of the catalog root path prefix.</param>
    /// <param name="tableRef">The canonical reference for discriminator computation.</param>
    /// <returns>The possibly-truncated and suffixed table segment.</returns>
    public static string ApplyPathBudget(
        string encodedSchema,
        string encodedTable,
        int catalogRootLength,
        CanonicalModel.Interchange.ObjectRef tableRef)
    {
        // "tables/<schema>/<table>/data.00001.csv"
        //  7 + schema + 1 + table + 1 + 15 = 24 + schema + table
        var totalLength = catalogRootLength
            + "tables/".Length + encodedSchema.Length + 1
            + encodedTable.Length + 1
            + "data.00001.csv".Length;

        if (totalLength <= MaxPathBudget)
        {
            return encodedTable;
        }

        // Truncate and add discriminator.
        var truncatedSchema = TruncateToCodePoints(encodedSchema, TruncatedSchemaLength);
        var truncatedTable = TruncateToCodePoints(encodedTable, TruncatedTableLength);
        var discriminator = tableRef.DiscriminatorHex();

        return truncatedTable + "-" + discriminator;
    }

    /// <summary>
    /// Returns the last-resort path for a name that cannot be encoded at all (rule 5).
    /// </summary>
    /// <param name="tableRef">The canonical reference whose hex becomes the folder name.</param>
    /// <returns>The path <c>_unmapped/&lt;64-hex&gt;</c>.</returns>
    public static string UnmappedPath(CanonicalModel.Interchange.ObjectRef tableRef)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tableRef.Value));
        return $"{UnmappedFolder}/{Convert.ToHexStringLower(hash)}";
    }

    /// <summary>
    /// Percent-encodes the unsafe set: <c>%</c> first (as <c>%25</c>), then <c>~</c> (as
    /// <c>%7E</c>), then every character illegal on Windows/macOS/Linux, plus U+0000–U+001F,
    /// plus leading/trailing <c>.</c> or space.
    /// </summary>
    private static string PercentEncodeUnsafe(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var sb = new StringBuilder(name.Length + 8);

        // % first, then ~.
        foreach (var c in name)
        {
            if (c == '%') sb.Append("%25");
            else if (c == '~') sb.Append("%7E");
            else if (IsIllegalChar(c)) sb.Append('%').AppendFormat("{0:X2}", (int)c);
            else sb.Append(c);
        }

        var result = sb.ToString();

        // Leading/trailing dot or space: encode them.
        if (result.Length > 0 && (result[0] == '.' || result[0] == ' '))
        {
            result = '%' + ((int)result[0]).ToString("X2") + result[1..];
        }

        if (result.Length > 0 && (result[^1] == '.' || result[^1] == ' '))
        {
            result = result[..^1] + '%' + ((int)result[^1]).ToString("X2");
        }

        return result;
    }

    /// <summary>
    /// If the encoded name is a reserved Windows device name (case-insensitively, ignoring any
    /// extension), prefixes it with <c>~</c>.
    /// </summary>
    private static string PrefixReservedDevice(string encoded)
    {
        // Strip extension for the device-name check.
        var dotIndex = encoded.IndexOf('.');
        var baseName = dotIndex >= 0 ? encoded[..dotIndex] : encoded;

        if (IsReservedDeviceName(baseName))
        {
            return "~" + encoded;
        }

        return encoded;
    }

    private static bool IsReservedDeviceName(string name) =>
        name.Equals("CON", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
        || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
        || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
        || IsComLpt(name);

    private static bool IsComLpt(string name)
    {
        if (name.Length < 4 || name.Length > 6) return false;

        var prefix = name[..3].ToUpperInvariant();
        if (prefix != "COM" && prefix != "LPT") return false;

        var suffix = name[3..];
        return suffix.Length >= 1
            && suffix.Length <= 3
            && suffix.All(char.IsDigit)
            && int.Parse(suffix) is >= 1 and <= 9;
    }

    private static bool IsIllegalChar(char c) =>
        c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
        or (>= '\0' and <= '\x1F');

    /// <summary>Returns the NFC-normalized form of the input.</summary>
    private static string NFC(string name) => name.Normalize(NormalizationForm.FormC);

    /// <summary>Truncates a string to the specified number of Unicode code points.</summary>
    private static string TruncateToCodePoints(string value, int maxCodePoints)
    {
        if (value.Length <= maxCodePoints) return value;

        var count = 0;
        var index = 0;
        while (index < value.Length && count < maxCodePoints)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                index += 2;
            }
            else
            {
                index += 1;
            }
            count++;
        }

        return value[..index];
    }
}
