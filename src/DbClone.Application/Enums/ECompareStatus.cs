namespace DbClone.Application.Enums;

/// <summary>
/// Represents the status of a comparison result between two database objects.
/// </summary>
public enum ECompareStatus
{
    Identical,

    /// <summary>
    /// Object matches on both sides but carries a non-structural note
    /// (e.g. schema owner differs). One level below <see cref="Different"/>:
    /// does not count as a difference between the databases.
    /// </summary>
    Notice,

    Different,

    MissingSource,

    MissingDest,

    /// <summary>Object exists in both databases but could not be compared (e.g. insufficient permissions).</summary>
    Skipped,

    Error
}
