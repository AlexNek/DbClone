namespace DbClone.UI.Services.GraphLayout;

/// <summary>A shared count of candidate routes flattened and checked. It, not the clock, ends the search.</summary>
internal sealed class GraphEvaluationBudget
{
    /// <summary>Creates a budget.</summary>
    /// <param name="limit">The most evaluations allowed.</param>
    /// <param name="metrics">Request observations shared by child budgets.</param>
    public GraphEvaluationBudget(int limit, GraphSearchMetrics? metrics = null)
    {
        Limit = limit;
        Metrics = metrics ?? new GraphSearchMetrics();
    }

    /// <summary>Gets observations shared by every phase of this request.</summary>
    public GraphSearchMetrics Metrics { get; }

    /// <summary>Gets the most evaluations allowed.</summary>
    public int Limit { get; }

    /// <summary>Gets how many evaluations have been spent.</summary>
    public int Used { get; private set; }

    /// <summary>Gets whether no evaluation is left.</summary>
    public bool IsExhausted => Used >= Limit;

    /// <summary>Records evaluations a child budget spent, never past the limit.</summary>
    /// <param name="count">How many evaluations were spent.</param>
    public void Add(int count)
    {
        Used = Math.Min(Limit, Used + Math.Max(0, count));
    }

    /// <summary>Spends one evaluation.</summary>
    /// <returns>Whether one was left to spend.</returns>
    public bool TrySpend()
    {
        if (Used >= Limit)
        {
            return false;
        }

        Used++;

        return true;
    }
}
