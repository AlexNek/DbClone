using System.Security.Cryptography;
using System.Text;

namespace CanonicalModel.Interchange;

/// <summary>
/// A qualified-name reference for an interchange object, spelled the way SQL spells it.
/// A human-readable qualified name such as <c>table:sel_test.orders</c> or
/// <c>column:public.users.email</c>.
/// </summary>
/// <remarks>
/// <para>
/// The grammar is <c>kind:part1.part2[.part3][(signature)]</c> where each part is a SQL identifier
/// that is wrapped in double quotes when it is empty, starts with a digit, or contains any character
/// outside <c>[A-Za-z0-9_]</c>. A double quote inside a quoted identifier is doubled.
/// </para>
/// <para>
/// Comparison is ordinal and case-sensitive: <c>table:sel_test.mixedcase</c> and
/// <c>table:sel_test.MixedCase</c> are two distinct objects.
/// </para>
/// </remarks>
public readonly record struct ObjectRef(string Value) : IComparable<ObjectRef>
{
    /// <summary>Creates a schema reference: <c>schema:&lt;name&gt;</c>.</summary>
    public static ObjectRef Schema(string name) => new($"schema:{QuoteIdentifier(name)}");

    /// <summary>Creates a table reference: <c>table:&lt;schema&gt;.&lt;name&gt;</c>.</summary>
    public static ObjectRef Table(string schema, string name) =>
        new($"table:{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}");

    /// <summary>Creates a view reference: <c>view:&lt;schema&gt;.&lt;name&gt;</c>.</summary>
    public static ObjectRef View(string schema, string name) =>
        new($"view:{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}");

    /// <summary>
    /// Creates a routine reference with a parameter list:
    /// <c>routine:&lt;schema&gt;.&lt;name&gt;(&lt;signature&gt;)</c>.
    /// </summary>
    public static ObjectRef Routine(string schema, string name, string signature) =>
        new($"routine:{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}({signature})");

    /// <summary>
    /// Creates a routine reference without a parameter list:
    /// <c>routine:&lt;schema&gt;.&lt;name&gt;</c>. Legal only where the name has one overload;
    /// several overloads raise <c>AmbiguousRoutineReference</c>.
    /// </summary>
    public static ObjectRef Routine(string schema, string name) =>
        new($"routine:{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}");

    /// <summary>Creates a sequence reference: <c>sequence:&lt;schema&gt;.&lt;name&gt;</c>.</summary>
    public static ObjectRef Sequence(string schema, string name) =>
        new($"sequence:{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}");

    /// <summary>Creates an extension reference: <c>extension:&lt;name&gt;</c>.</summary>
    public static ObjectRef Extension(string name) => new($"extension:{QuoteIdentifier(name)}");

    /// <summary>
    /// Creates a type reference (enum, composite, domain):
    /// <c>type:&lt;schema&gt;.&lt;name&gt;</c>.
    /// </summary>
    public static ObjectRef Type(string schema, string name) =>
        new($"type:{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}");

    /// <summary>Creates a publication reference: <c>publication:&lt;name&gt;</c>.</summary>
    public static ObjectRef Publication(string name) =>
        new($"publication:{QuoteIdentifier(name)}");

    /// <summary>Creates a subscription reference: <c>subscription:&lt;name&gt;</c>.</summary>
    public static ObjectRef Subscription(string name) =>
        new($"subscription:{QuoteIdentifier(name)}");

    /// <summary>
    /// Creates a column reference: <c>column:&lt;schema&gt;.&lt;table&gt;.&lt;name&gt;</c>.
    /// </summary>
    public static ObjectRef Column(string schema, string table, string name) =>
        new($"column:{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}.{QuoteIdentifier(name)}");

    /// <summary>
    /// Creates a sub-object reference (index, constraint, trigger, policy):
    /// <c>&lt;kind&gt;:&lt;schema&gt;.&lt;table&gt;.&lt;name&gt;</c>.
    /// </summary>
    public static ObjectRef SubObject(string kind, string schema, string table, string name) =>
        new($"{kind}:{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}.{QuoteIdentifier(name)}");

    /// <summary>Creates a role reference: <c>role:&lt;name&gt;</c>.</summary>
    public static ObjectRef Role(string name) => new($"role:{QuoteIdentifier(name)}");

    /// <summary>Creates a grant reference: <c>grant:&lt;id-or-name&gt;</c>.</summary>
    public static ObjectRef Grant(string idOrName) => new($"grant:{idOrName}");

    /// <summary>Creates a policy reference: <c>policy:&lt;schema&gt;.&lt;table&gt;.&lt;name&gt;</c>.</summary>
    public static ObjectRef Policy(string schema, string table, string name) =>
        new($"policy:{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}.{QuoteIdentifier(name)}");

    /// <summary>Gets the kind prefix (e.g. "table", "schema", "column") from the reference.</summary>
    public string Kind
    {
        get
        {
            var colonIndex = Value.IndexOf(':');
            return colonIndex < 0 ? string.Empty : Value[..colonIndex];
        }
    }

    /// <summary>
    /// Extracts the schema name from a qualified reference, or <see langword="null"/> when the
    /// reference kind does not carry a schema (e.g. <c>schema:</c>, <c>extension:</c>, <c>role:</c>).
    /// </summary>
    public string? SchemaName()
    {
        var afterKind = Value.AsSpan()[(Kind.Length + 1)..];
        var firstDot = afterKind.IndexOf('.');
        if (firstDot <= 0) return null;
        return UnquoteIdentifier(firstDot < afterKind.Length ? afterKind[..firstDot].ToString() : afterKind.ToString());
    }

    /// <summary>
    /// Extracts the object name (the last identifier segment, before any parenthesised signature)
    /// from the reference.
    /// </summary>
    public string ObjectName()
    {
        var afterKind = Value.AsSpan()[(Kind.Length + 1)..];

        // Strip any parenthesised signature for routines.
        var parenIndex = afterKind.IndexOf('(');
        if (parenIndex >= 0) afterKind = afterKind[..parenIndex];

        // The object name is the last dot-separated segment.
        var lastDot = afterKind.LastIndexOf('.');
        var raw = lastDot >= 0 ? afterKind[(lastDot + 1)..].ToString() : afterKind.ToString();
        return UnquoteIdentifier(raw);
    }

    /// <summary>
    /// Computes the first 8 lowercase hex characters of the SHA-256 hash of the reference string.
    /// Used by the naming algorithm to disambiguate case-collision paths.
    /// </summary>
    public string DiscriminatorHex()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Value));
        return Convert.ToHexStringLower(hash, 0, 4); // first 4 bytes = 8 hex chars
    }

    /// <summary>Ordinal case-sensitive comparison.</summary>
    public int CompareTo(ObjectRef other) =>
        string.Compare(Value, other.Value, StringComparison.Ordinal);

    /// <summary>Returns the reference string as it appears in a file.</summary>
    public override string ToString() => Value;

    /// <summary>
    /// Wraps a SQL identifier in double quotes when it is empty, starts with a digit, or contains
    /// any character outside <c>[A-Za-z0-9_]</c>. A <c>"</c> inside the identifier is doubled.
    /// </summary>
    internal static string QuoteIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return "\"\"";

        var needsQuoting = false;
        if (char.IsDigit(name[0]))
        {
            needsQuoting = true;
        }
        else
        {
            foreach (var c in name)
            {
                if (!IsSafeIdentifierChar(c))
                {
                    needsQuoting = true;
                    break;
                }
            }
        }

        if (!needsQuoting) return name;

        return "\"" + name.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// Removes the double-quote wrapping from a SQL identifier and un-doubles any internal
    /// <c>""</c> sequences.
    /// </summary>
    internal static string UnquoteIdentifier(string name)
    {
        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
        {
            return name[1..^1].Replace("\"\"", "\"");
        }

        return name;
    }

    private static bool IsSafeIdentifierChar(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';
}
