using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphAnalysis;

/// <summary>
/// The groups of two or more tables that reach each other. A group is numbered, and one larger than
/// the collapse limit is decided here to start drawn behind a supernode — so no later stage ever
/// recounts a component (D4 step 2, FR16, D8a).
/// </summary>
/// <remarks>
/// The decision is a display state, never an aggregate: every member keeps its node, its detail and
/// its register rows, and expanding the supernode reveals what was already there (D7).
/// </remarks>
internal static class GraphCyclicGroups
{
    /// <summary>Groups the nodes by the strongly-connected component they belong to.</summary>
    /// <param name="seeds">The ordered nodes.</param>
    /// <param name="component">For each node, the component it landed in.</param>
    /// <returns>The groups of two or more, numbered from one, in node order.</returns>
    internal static List<CatalogCyclicGroup> Build(
        IReadOnlyList<GraphNodeSeed> seeds,
        int[] component)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(component);

        var membersOf = new Dictionary<int, List<int>>();
        for (var node = 0; node < seeds.Count; node++)
        {
            if (!membersOf.TryGetValue(component[node], out var members))
            {
                members = [];
                membersOf[component[node]] = members;
            }

            members.Add(node);
        }

        var groups = new List<CatalogCyclicGroup>();
        foreach (var members in membersOf.Values.Where(members => members.Count >= 2).OrderBy(members => members[0]))
        {
            groups.Add(new CatalogCyclicGroup(
                groups.Count + 1,
                $"↺ {members.Count}",
                members,
                members.Count > CatalogGraphAnalyzer.CollapsedComponentSizeLimit));
        }

        return groups;
    }

    /// <summary>Inverts the groups into one answer per node.</summary>
    /// <param name="groups">The cyclic groups.</param>
    /// <param name="nodeCount">How many nodes the graph has.</param>
    /// <returns>For each node, its group number, or <see langword="null"/>.</returns>
    internal static int?[] GroupOfNode(IReadOnlyList<CatalogCyclicGroup> groups, int nodeCount)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var groupOfNode = new int?[nodeCount];
        foreach (var group in groups)
        {
            foreach (var member in group.MemberNodeIndexes)
            {
                groupOfNode[member] = group.Number;
            }
        }

        return groupOfNode;
    }

    /// <summary>The other tables of a node's group, by display name.</summary>
    /// <param name="node">The node.</param>
    /// <param name="groupNumber">Its group, if it is in one.</param>
    /// <param name="groups">The cyclic groups.</param>
    /// <param name="seeds">The ordered nodes.</param>
    /// <returns>The group's other members, itself excluded.</returns>
    internal static IReadOnlyList<string> OtherMembers(
        int node,
        int? groupNumber,
        IReadOnlyList<CatalogCyclicGroup> groups,
        IReadOnlyList<GraphNodeSeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(seeds);

        if (groupNumber is null)
        {
            return [];
        }

        var members = new List<string>();
        foreach (var member in groups[groupNumber.Value - 1].MemberNodeIndexes)
        {
            if (member != node)
            {
                members.Add(seeds[member].DisplayName);
            }
        }

        return members;
    }
}
