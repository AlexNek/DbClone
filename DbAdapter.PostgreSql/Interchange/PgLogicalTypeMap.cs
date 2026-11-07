using CanonicalModel.Interchange;

namespace DbAdapter.PostgreSql.Interchange;

/// <summary>
/// The PostgreSQL half of the interchange type table: a native type name as reported by
/// <c>format_type()</c> in, one <see cref="ELogicalType"/> out.
/// <para>
/// The format stores the <see cref="ELogicalType"/>/<see cref="NativeType"/> pair and never
/// guesses, so an unmapped name stays <see cref="ELogicalType.Unknown"/> and is carried by its
/// native text alone (refactors/04/interchangeFormatDesign.md, "Where type mapping lives").
/// Names are matched after normalization; the PostgreSQL-prefixed forms (<c>pg_catalog.</c>)
/// and quoted identifiers are accepted because the metadata reader can report either.
/// </para>
/// </summary>
internal static class PgLogicalTypeMap
{
    /// <summary>
    /// Classifies one PostgreSQL type name. <paramref name="userDefinedEnumTypes"/> is the set of
    /// schema-qualified and unqualified names of the enum types present in the database; a name in
    /// that set is an <see cref="ELogicalType.Enum"/>, any other unknown name is
    /// <see cref="ELogicalType.Unknown"/>.
    /// </summary>
    public static ELogicalType Classify(string? nativeType, IReadOnlySet<string>? userDefinedEnumTypes = null)
    {
        var (baseName, isArray) = SplitBaseName(nativeType);
        if (isArray)
        {
            return ELogicalType.Array;
        }

        if (baseName.Length == 0)
        {
            return ELogicalType.Unknown;
        }

        // format_type() folds the declared modifiers into the name ("character varying(80)"), so
        // the table is matched on the base type alone; ReadModifiers reports the sizes separately.
        var typeName = StripModifiers(baseName);

        if (userDefinedEnumTypes is not null && ContainsName(userDefinedEnumTypes, typeName))
        {
            return ELogicalType.Enum;
        }

        // Range types are named after their subtype, so they need their own branch before the
        // integer/date lookups would otherwise claim them.
        if (IsRangeName(typeName))
        {
            return ELogicalType.Range;
        }

        return typeName switch
        {
            "bool" or "boolean" => ELogicalType.Boolean,
            "int2" or "smallint" or "smallserial" => ELogicalType.Integer16,
            "int4" or "integer" or "int" or "serial" or "serial4" => ELogicalType.Integer32,
            "int8" or "bigint" or "bigserial" or "serial8" => ELogicalType.Integer64,
            "numeric" or "decimal" or "money" => ELogicalType.Decimal,
            "float4" or "real" => ELogicalType.Float32,
            "float8" or "double precision" or "float" => ELogicalType.Float64,
            "text" or "citext" or "varchar" or "character varying" or "char" or "character"
                or "bpchar" or "name" => ELogicalType.String,
            "bytea" => ELogicalType.Binary,
            "date" => ELogicalType.Date,
            "time" or "time without time zone" => ELogicalType.Time,
            "timetz" or "time with time zone" => ELogicalType.TimeWithTimeZone,
            "timestamp" or "timestamp without time zone" => ELogicalType.Timestamp,
            "timestamptz" or "timestamp with time zone" or "abstime" => ELogicalType.TimestampWithTimeZone,
            "interval" => ELogicalType.Interval,
            "uuid" => ELogicalType.Uuid,
            "json" or "jsonb" => ELogicalType.Json,
            "xml" => ELogicalType.Xml,
            "inet" or "cidr" or "macaddr" or "macaddr8" => ELogicalType.Network,
            "bit" or "varbit" or "bit varying" => ELogicalType.BitString,
            "point" or "line" or "lseg" or "box" or "path" or "polygon" or "circle"
                or "geometry" or "geography" => ELogicalType.Geometry,
            "vector" => ELogicalType.Vector,
            _ => ELogicalType.Unknown,
        };
    }

    /// <summary>
    /// Tells whether a column's text must be normalized to UTC on the way out. The format stores
    /// <see cref="ELogicalType.TimestampWithTimeZone"/> as a UTC instant, while PostgreSQL's own
    /// text rendering prints the session offset, so the value has to be converted in the query.
    /// </summary>
    public static bool IsUtcNormalized(string? nativeType) =>
        Classify(nativeType) == ELogicalType.TimestampWithTimeZone;

    /// <summary>
    /// Reads the declared modifiers out of a type name: length for character types, precision and
    /// scale for exact numerics. Returns all three null when the type carries none.
    /// </summary>
    public static (int? Length, int? Precision, int? Scale) ReadModifiers(string? nativeType)
    {
        var (baseName, _) = SplitBaseName(nativeType);
        var open = baseName.IndexOf('(');
        if (open < 0)
        {
            return (null, null, null);
        }

        var args = baseName[(open + 1)..].TrimEnd(')');
        var parts = args.Split(',');

        // character(n) / bit(n) declare a length; numeric(p,s) declares precision and scale.
        if (IsCharFamily(WithoutModifiers(baseName)) || IsBitFamily(WithoutModifiers(baseName)))
        {
            return (TryInt(parts, 0), null, null);
        }

        if (IsNumericFamily(WithoutModifiers(baseName)))
        {
            return (null, TryInt(parts, 0), TryInt(parts, 1));
        }

        // vector(dimensions) is the remaining sized type, and the dimension is its length.
        return WithoutModifiers(baseName) switch
        {
            "vector" => (TryInt(parts, 0), null, null),
            _ => (null, null, null),
        };
    }

    /// <summary>
    /// Strips the trailing array markers and the modifier parentheses so both can be inspected
    /// independently. <c>numeric(10,2)[]</c> yields ("numeric(10,2)", true).
    /// </summary>
    private static (string BaseName, bool IsArray) SplitBaseName(string? nativeType)
    {
        var text = (nativeType ?? string.Empty).Trim();
        var isArray = false;

        while (text.EndsWith("[]", StringComparison.Ordinal))
        {
            isArray = true;
            text = text[..^2].TrimEnd();
        }

        // A trailing [] inside the brackets of a declared size ("char(2)[]") is handled above;
        // pg reports multi-dimensional arrays as text[][] so only the flag matters here.
        if (text.StartsWith("pg_catalog.", StringComparison.OrdinalIgnoreCase))
        {
            text = text["pg_catalog.".Length..];
        }

        return (Unquote(text).Trim(), isArray);
    }

    /// <summary>
    /// Removes the declared size from a <c>format_type()</c> result. The parentheses are not always
    /// trailing: the date/time types write them before the zone phrase
    /// ("timestamp(6) with time zone"), so the two halves are joined — cutting the name short would
    /// collapse an instant onto a wall clock.
    /// </summary>
    private static string StripModifiers(string baseName)
    {
        var open = baseName.IndexOf('(');
        var close = baseName.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return baseName.Trim();
        }

        return $"{baseName[..open]}{baseName[(close + 1)..]}".Trim();
    }

    private static string WithoutModifiers(string baseName) =>
        StripModifiers(baseName).ToLowerInvariant();

    private static string Unquote(string identifier) =>
        identifier.StartsWith('"') && identifier.EndsWith('"')
            ? identifier.Trim('"')
            : identifier;

    private static bool IsRangeName(string baseName) =>
        WithoutModifiers(baseName) is "int4range" or "int8range" or "numrange"
            or "tsrange" or "tstzrange" or "daterange";

    private static bool IsCharFamily(string baseName) =>
        baseName is "varchar" or "character varying" or "char" or "character" or "bpchar";

    private static bool IsBitFamily(string baseName) =>
        baseName is "bit" or "varbit" or "bit varying";

    private static bool IsNumericFamily(string baseName) =>
        baseName is "numeric" or "decimal";

    /// <summary>
    /// Matches a <c>format_type()</c> result against a set holding both the unqualified and the
    /// schema-qualified form of each name. Quoted mixed-case names compare case-sensitively, so
    /// the set is expected to be ordinal; unquoted names reach PostgreSQL already lowercased.
    /// </summary>
    private static bool ContainsName(IReadOnlySet<string> names, string baseName)
    {
        var withoutSchema = baseName;
        var dot = baseName.LastIndexOf('.');
        if (dot >= 0)
        {
            withoutSchema = baseName[(dot + 1)..];
        }

        return names.Contains(baseName) || names.Contains(withoutSchema);
    }

    private static int? TryInt(string[] parts, int index) =>
        index < parts.Length && int.TryParse(parts[index].Trim(), out var value)
            ? value
            : null;
}
