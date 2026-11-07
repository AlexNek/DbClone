namespace CanonicalModel.Interchange;

/// <summary>
/// The logical type of a column or value in the interchange model.
/// This is the portable class, independent of any engine's native type names.
/// </summary>
public enum ELogicalType
{
    /// <summary>Boolean true/false.</summary>
    Boolean,

    /// <summary>16-bit signed integer.</summary>
    Integer16,

    /// <summary>32-bit signed integer.</summary>
    Integer32,

    /// <summary>64-bit signed integer.</summary>
    Integer64,

    /// <summary>Arbitrary-precision decimal.</summary>
    Decimal,

    /// <summary>32-bit floating point.</summary>
    Float32,

    /// <summary>64-bit floating point.</summary>
    Float64,

    /// <summary>Variable-length character string.</summary>
    String,

    /// <summary>Binary data.</summary>
    Binary,

    /// <summary>Calendar date (no time, no offset).</summary>
    Date,

    /// <summary>Time of day (no date, no offset).</summary>
    Time,

    /// <summary>Time of day with time zone offset.</summary>
    TimeWithTimeZone,

    /// <summary>Date and time (no offset).</summary>
    Timestamp,

    /// <summary>Date and time with time zone (normalized to UTC).</summary>
    TimestampWithTimeZone,

    /// <summary>Time interval (ISO-8601 duration).</summary>
    Interval,

    /// <summary>Universally unique identifier (128-bit).</summary>
    Uuid,

    /// <summary>JSON document.</summary>
    Json,

    /// <summary>XML document.</summary>
    Xml,

    /// <summary>User-defined enum.</summary>
    Enum,

    /// <summary>Array (one or more dimensions).</summary>
    Array,

    /// <summary>Range (lower, upper, inclusivity).</summary>
    Range,

    /// <summary>Geometric/spatial data.</summary>
    Geometry,

    /// <summary>Vector (fixed-dimension numeric array for ML).</summary>
    Vector,

    /// <summary>Network address (IPv4/IPv6, CIDR).</summary>
    Network,

    /// <summary>Bit string.</summary>
    BitString,

    /// <summary>Unknown or unmapped type (preserved as native text).</summary>
    Unknown,
}
