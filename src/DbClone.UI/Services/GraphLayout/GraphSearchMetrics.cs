namespace DbClone.UI.Services.GraphLayout;

/// <summary>Request-local observations only; these counts never influence search or selection.</summary>
internal sealed class GraphSearchMetrics
{
    /// <summary>Gets the largest materialized candidate list for one relation.</summary>
    public int PeakRouteCandidates { get; private set; }

    /// <summary>Gets the largest conflict set actually searched, including its failed relation.</summary>
    public int PeakConflictSetSize { get; private set; }

    /// <summary>Gets the number of conflict sets searched, including growth attempts.</summary>
    public int ConflictSets { get; private set; }

    /// <summary>Gets the number of accepted repair and crossing-reduction placement moves across all seeds.</summary>
    public int RepairMoves { get; private set; }

    /// <summary>Observes a candidate list without retaining any geometry.</summary>
    public void ObserveCandidates(int count) => PeakRouteCandidates = Math.Max(PeakRouteCandidates, count);

    /// <summary>Observes a conflict set after truncation and before search.</summary>
    public void ObserveConflictSet(int count)
    {
        PeakConflictSetSize = Math.Max(PeakConflictSetSize, count);
        ConflictSets++;
    }

    /// <summary>Records an accepted placement move, not a trial or a rejected candidate.</summary>
    public void ObserveRepairMove() => RepairMoves++;
}
