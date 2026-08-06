namespace DbClone.Application.Enums;

/// <summary>
/// Controls how deep the validation goes when comparing source and destination.
/// </summary>
public enum EVerifyMode
{
    /// <summary>Compare row counts per table. Fast (seconds).</summary>
    RowCount,

    /// <summary>Compare data checksums per table. Medium speed, verifies actual data content.</summary>
    Checksum,

    /// <summary>Row-by-row comparison. Slow, for critical migrations.</summary>
    Full
}
