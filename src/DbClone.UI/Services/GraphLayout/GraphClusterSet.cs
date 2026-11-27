namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The communities one connected component splits into: the cluster number of each node, the members of
/// each cluster in ascending node order, and the modularity the split reaches. The arranger and the seed
/// read this; nothing here knows about card sizes or coordinates.
/// </summary>
/// <param name="ClusterOf">The cluster number of each local node index.</param>
/// <param name="Members">The nodes of every cluster, ascending, parallel to the cluster number.</param>
/// <param name="Modularity">The modularity of the reported partition. Below the guard the detector returns null.</param>
internal sealed record GraphClusterSet(
    IReadOnlyList<int> ClusterOf,
    IReadOnlyList<IReadOnlyList<int>> Members,
    double Modularity);
