namespace DatabaseInterchange.Report;

/// <summary>
/// The bounds of the derived views and the formatting constants of the catalog report.
/// </summary>
/// <remarks>
/// <para>
/// Every number here describes what a <em>derived</em> view is (a walk, a top-N ranking, a restatement),
/// never how much of the captured data may be printed. Nothing about the captured facts is capped: the
/// register, the cards and the per-table listing print every constraint and every table, so a cut cannot
/// hide a relation (FR7b). Should a ranking ever hide a fact that no other section prints, the ranking
/// becomes a listing and its bound goes with it.
/// </para>
/// <para>
/// These are format-level constants, not settings (D7): the only user preference this feature has is
/// whether the report is written at all, and it lives in the application, not here.
/// </para>
/// </remarks>
internal static class ReportBudget
{
    /// <summary>Gets the largest group an outline is attempted for; a bigger group gets the refusal line (§5.6).</summary>
    public const int MaxOutlineTables = 10;

    /// <summary>Gets how many rows each coupling ranking prints (§5.4).</summary>
    public const int MaxCouplingRows = 20;

    /// <summary>Gets how many lines the observation section may hold (§5.10).</summary>
    public const int MaxObservations = 12;

    /// <summary>Gets how many tables the "top ten tables hold" prose line counts (FR3).</summary>
    public const int TopTablesForShare = 10;

    /// <summary>Gets the fan-in tables the observation section names (§5.10).</summary>
    public const int TopFanInInObservations = 3;

    /// <summary>
    /// Gets the UTC timestamp format (D9.2). Whole seconds only, so the report's stamp matches the
    /// instant <c>export.json</c> records at the resolution a reader can compare by eye.
    /// </summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Gets the separator used between facts on one prose line (§6).</summary>
    public const string FactSeparator = " · ";
}
