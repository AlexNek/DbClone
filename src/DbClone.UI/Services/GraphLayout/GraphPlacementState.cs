namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The mutable placement repair works on: every card's centre and width, grouped by connected component.
/// Components are drawn independently and packed, so a move or an expansion inside one component never has
/// to know where another stands.
/// </summary>
internal sealed class GraphPlacementState
{
    private GraphPlacementState(GraphLayoutProfile profile, double[] widths, double[] centreX, double[] centreY)
    {
        Profile = profile;
        Widths = widths;
        CentreX = centreX;
        CentreY = centreY;
    }

    /// <summary>Gets the graph facts.</summary>
    public GraphLayoutProfile Profile { get; }

    /// <summary>Gets each card's width, by node index.</summary>
    public double[] Widths { get; }

    /// <summary>Gets each card's centre x, by node index.</summary>
    public double[] CentreX { get; }

    /// <summary>Gets each card's centre y, by node index.</summary>
    public double[] CentreY { get; }

    /// <summary>Starts from a draft.</summary>
    /// <param name="draft">The seed's placement.</param>
    /// <param name="profile">The graph facts.</param>
    /// <returns>The state.</returns>
    public static GraphPlacementState From(GraphDraftLayout draft, GraphLayoutProfile profile) =>
        new(
            profile,
            draft.Nodes.Select(node => node.Width).ToArray(),
            draft.Nodes.Select(node => node.Centre.X).ToArray(),
            draft.Nodes.Select(node => node.Centre.Y).ToArray());

    /// <summary>Copies the state so a trial move never touches the accepted placement.</summary>
    /// <returns>The copy.</returns>
    public GraphPlacementState Clone() => new(Profile, (double[])Widths.Clone(), (double[])CentreX.Clone(), (double[])CentreY.Clone());

    /// <summary>Packs the components into absolute placements.</summary>
    /// <param name="name">The seed's name.</param>
    /// <returns>The draft layout.</returns>
    public GraphDraftLayout Assemble(string name) =>
        GraphComponentPacker.Pack(
            name,
            Widths,
            Profile.ComponentNodes
                .Select(nodes => GraphLocalLayout.FromCentres(
                    nodes,
                    nodes.Select(node => CentreX[node]).ToArray(),
                    nodes.Select(node => CentreY[node]).ToArray(),
                    Widths))
                .ToArray());

    /// <summary>Gets a text that is equal for equal placements, so a visited placement is never tried twice.</summary>
    /// <returns>The signature.</returns>
    public string Signature() =>
        string.Join(
            ";",
            Enumerable.Range(0, Widths.Length).Select(node => FormattableString.Invariant($"{Widths[node]:0.#},{CentreX[node]:0.#},{CentreY[node]:0.#}")));

    /// <summary>Widens a card around its own centre.</summary>
    /// <param name="node">The card.</param>
    /// <param name="width">The new width.</param>
    public void Widen(int node, double width)
    {
        Widths[node] = Math.Max(Widths[node], width);
    }

    /// <summary>Moves a card.</summary>
    /// <param name="node">The card.</param>
    /// <param name="dx">The horizontal shift.</param>
    /// <param name="dy">The vertical shift.</param>
    public void Move(int node, double dx, double dy)
    {
        CentreX[node] += dx;
        CentreY[node] += dy;
    }

    /// <summary>Scales a component's centres about its own centroid, which leaves every card its size and opens the lanes between them.</summary>
    /// <param name="component">The component number.</param>
    /// <param name="factor">The scale, above 1 to spread.</param>
    public void Expand(int component, double factor)
    {
        var nodes = Profile.ComponentNodes[component];
        var (meanX, meanY) = (nodes.Average(node => CentreX[node]), nodes.Average(node => CentreY[node]));
        foreach (var node in nodes)
        {
            CentreX[node] = meanX + ((CentreX[node] - meanX) * factor);
            CentreY[node] = meanY + ((CentreY[node] - meanY) * factor);
        }
    }

    /// <summary>Removes any overlap inside one component after a move or a widening.</summary>
    /// <param name="component">The component number.</param>
    public void Settle(int component)
    {
        var nodes = Profile.ComponentNodes[component];
        var (x, y) = (nodes.Select(node => CentreX[node]).ToArray(), nodes.Select(node => CentreY[node]).ToArray());
        GraphCardSeparator.Settle(nodes, x, y, Widths);
        for (var at = 0; at < nodes.Count; at++)
        {
            CentreX[nodes[at]] = x[at];
            CentreY[nodes[at]] = y[at];
        }
    }

    /// <summary>The summed distance every card's centre has moved from another state.</summary>
    /// <param name="other">The state to compare with.</param>
    /// <returns>The total displacement.</returns>
    public double DisplacementFrom(GraphPlacementState other) =>
        Enumerable.Range(0, Widths.Length).Sum(node => Math.Abs(CentreX[node] - other.CentreX[node]) + Math.Abs(CentreY[node] - other.CentreY[node]));
}
