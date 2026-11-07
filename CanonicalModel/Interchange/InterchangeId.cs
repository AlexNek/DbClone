using System.Security.Cryptography;
using System.Text;

namespace CanonicalModel.Interchange;

/// <summary>
/// A content-derived identity for interchange objects. Computed as SHA-256 over
/// length-prefixed UTF-8 components (kind, schema-name, object-name, parent-Id).
/// </summary>
/// <param name="Hex">Lowercase hexadecimal SHA-256 hash (64 characters).</param>
public readonly record struct InterchangeId(string Hex) : IEquatable<InterchangeId>, IComparable<InterchangeId>
{
    /// <summary>Creates an <see cref="InterchangeId"/> from the canonical fields.</summary>
    /// <param name="kind">The object kind (e.g., "table", "view", "function").</param>
    /// <param name="schemaName">The schema name (empty for schema-less objects).</param>
    /// <param name="objectName">The object name.</param>
    /// <param name="parentId">The parent object's Id (empty for top-level objects).</param>
    /// <returns>A new <see cref="InterchangeId"/> computed from the inputs.</returns>
    public static InterchangeId Compute(string kind, string schemaName, string objectName, InterchangeId parentId = default)
    {
        using var sha256 = SHA256.Create();
        var bytes = new MemoryStream();

        // Length-prefixed encoding: uint32 big-endian byte length followed by UTF-8 bytes
        WriteLengthPrefixed(bytes, kind);
        WriteLengthPrefixed(bytes, schemaName);
        WriteLengthPrefixed(bytes, objectName);
        WriteLengthPrefixed(bytes, parentId.Hex ?? string.Empty);

        bytes.Position = 0;
        var hash = sha256.ComputeHash(bytes);
        return new InterchangeId(Convert.ToHexStringLower(hash));
    }

    private static void WriteLengthPrefixed(MemoryStream stream, string value)
    {
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var lengthBytes = BitConverter.GetBytes(valueBytes.Length);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(lengthBytes);
        }
        stream.Write(lengthBytes, 0, 4);
        stream.Write(valueBytes, 0, valueBytes.Length);
    }

    /// <summary>Gets whether this Id is the default (empty) value.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Hex);

    /// <inheritdoc />
    public bool Equals(InterchangeId other) =>
        string.Equals(Hex, other.Hex, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override int GetHashCode() =>
        Hex is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Hex);

    /// <inheritdoc />
    public int CompareTo(InterchangeId other) =>
        string.Compare(Hex, other.Hex, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the lowercase hex string.</summary>
    public override string ToString() => Hex ?? string.Empty;

    /// <summary>Implicit conversion to string for convenience.</summary>
    public static implicit operator string(InterchangeId id) => id.ToString();
}
