namespace DatabaseInterchange.Format;

/// <summary>
/// The class of format detection: how the magic bytes are validated.
/// </summary>
/// <remarks>
/// The vocabulary is the design's (refactors/12/largeValueContainersDesign.md §3.1): every entry of the
/// naming table is classified by how definitive its magic is, and there is no fourth class — a
/// probabilistic entry is banned, because a wrong name mislabels the user's data and turns the
/// name-against-bytes check (§6.8) into a corruption alarm over healthy files.
/// </remarks>
internal enum FormatClass : byte
{
    /// <summary>The magic alone is definitive.</summary>
    Exact,

    /// <summary>The magic is short or shared; header internals must also be asserted.</summary>
    Validated,

    /// <summary>A container format named by its first entries (ZIP local headers).</summary>
    Probe,
}
