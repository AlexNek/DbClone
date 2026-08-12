namespace DbClone.Application.DTOs;

/// <summary>
/// Well-known keys for <see cref="StageDetail.Properties"/>.
/// Prevents magic strings across producers and renderers.
/// </summary>
public static class PropKeys
{
    /// <summary>ECompareSide value (Source or Destination).</summary>
    public const string Side = "side";

    /// <summary>Reason text (exception message, skip reason).</summary>
    public const string Reason = "reason";

    /// <summary>Generic count (int).</summary>
    public const string Count = "count";

    /// <summary>Expected count (int).</summary>
    public const string Expected = "expected";

    /// <summary>Actual count (int).</summary>
    public const string Actual = "actual";

    /// <summary>Matched count (int).</summary>
    public const string Matched = "matched";

    /// <summary>Failed count (int).</summary>
    public const string Failed = "failed";

    /// <summary>EDatabaseObjectType value.</summary>
    public const string ObjectType = "objectType";

    /// <summary>Source row count (long).</summary>
    public const string SourceRows = "sourceRows";

    /// <summary>Destination row count (long).</summary>
    public const string DestRows = "destRows";

    /// <summary>Version string.</summary>
    public const string Version = "version";

    /// <summary>Extension name.</summary>
    public const string Extension = "extension";

    /// <summary>Total count (int).</summary>
    public const string Total = "total";

    /// <summary>Skipped count (int).</summary>
    public const string Skipped = "skipped";

    /// <summary>Detail suffix (validation context, e.g. " (100 rows)").</summary>
    public const string Detail = "detail";

    /// <summary>Copy mode description.</summary>
    public const string Mode = "mode";

    /// <summary>Host label (host:port/database).</summary>
    public const string Host = "host";
}
