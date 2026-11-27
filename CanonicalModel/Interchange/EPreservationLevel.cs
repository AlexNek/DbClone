namespace CanonicalModel.Interchange;

/// <summary>
/// Row order modes for data export/import.
/// </summary>
public enum ERowOrder
{
    /// <summary>Order by primary key (or unique constraint if no PK).</summary>
    PrimaryKey,

    /// <summary>Order by all columns in ordinal order, compared by canonical text bytes.</summary>
    CanonicalValueOrder,

    /// <summary>Whatever order the source produced. Not repeatable.</summary>
    SourceOrder,
}

/// <summary>
/// Preservation level of an object in the catalog.
/// </summary>
public enum EPreservationLevel
{
    /// <summary>Canonical fields plus native definition, no known loss.</summary>
    Full,

    /// <summary>Preserved as native text only.</summary>
    Native,

    /// <summary>Some properties could not be captured.</summary>
    Partial,

    /// <summary>Only identity and name; the body is elsewhere or absent.</summary>
    Reference,
}
