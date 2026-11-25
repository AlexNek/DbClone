namespace DbClone.UI.Services.GraphLayout;

/// <summary>Occupied community regions: a star's central family is its hub and independent branches, not one solid box.</summary>
internal static class GraphClusterRegions
{
    internal static IReadOnlyList<(int Community, IReadOnlyList<int> Members)> Split(
        GraphLayoutProfile profile, GraphClusterSet clusters, int? hub)
    {
        var regions = new List<(int Community, IReadOnlyList<int> Members)>();
        for (var group = 0; group < clusters.Members.Count; group++)
        {
            if (hub is { } root && clusters.Members[group].Contains(root))
            {
                regions.Add((group, new[] { root }));
                regions.AddRange(GraphStarPlacementMoves.Branches(profile, clusters, root).Select(branch => (group, branch)));
            }
            else regions.Add((group, clusters.Members[group]));
        }
        return regions;
    }

    internal static IReadOnlyList<(int Community, IReadOnlyList<int> Members)> Detect(GraphLayoutProfile profile)
    {
        var regions = new List<(int Community, IReadOnlyList<int> Members)>();
        var groups = profile.ClusterGroups;
        foreach (var component in groups.Select(group => profile.Component[group[0]]).Distinct())
        {
            var indexes = Enumerable.Range(0, groups.Count).Where(at => profile.Component[groups[at][0]] == component).ToArray();
            var members = indexes.Select(at => groups[at]).ToArray();
            var labels = new int[profile.NodeCount];
            for (var group = 0; group < members.Length; group++)
                foreach (var node in members[group]) labels[node] = group;
            var clusters = new GraphClusterSet(labels, members, 0);
            var hub = members.Length >= 4 ? GraphClusterStarLayout.FindHub(profile, clusters) : null;
            regions.AddRange(Split(profile, clusters, hub).Select(region => (indexes[region.Community], region.Members)));
        }
        return regions;
    }
}
