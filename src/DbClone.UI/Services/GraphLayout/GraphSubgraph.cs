namespace DbClone.UI.Services.GraphLayout;

/// <summary>One connected component as a graph of its own, with the maps back to the whole graph.</summary>
/// <param name="Profile">The component's own profile, with node and edge indexes starting at zero.</param>
/// <param name="Nodes">The whole graph's node index of each local node.</param>
/// <param name="Edges">The whole graph's edge index of each local edge.</param>
internal sealed record GraphSubgraph(GraphLayoutProfile Profile, IReadOnlyList<int> Nodes, IReadOnlyList<int> Edges);
