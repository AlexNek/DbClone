namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// What repair minimises, in order: ends without a port, relations without a route, then the cards and
/// routes that block them. It is a ranking for intermediate states, never a score: a state with any
/// non-zero field is not a layout.
/// </summary>
/// <param name="PortFailures">Ends that found no border slot.</param>
/// <param name="Unrouted">Relations without an admissible route.</param>
/// <param name="Blockers">Blocking cards and routes named by the remaining failures.</param>
internal readonly record struct GraphRepairObjective(int PortFailures, int Unrouted, int Blockers) : IComparable<GraphRepairObjective>
{
    /// <summary>Gets whether nothing is left to repair.</summary>
    public bool IsZero => PortFailures == 0 && Unrouted == 0 && Blockers == 0;

    /// <inheritdoc/>
    public int CompareTo(GraphRepairObjective other)
    {
        var byPorts = PortFailures.CompareTo(other.PortFailures);
        if (byPorts != 0)
        {
            return byPorts;
        }

        var byRoutes = Unrouted.CompareTo(other.Unrouted);

        return byRoutes != 0 ? byRoutes : Blockers.CompareTo(other.Blockers);
    }
}
