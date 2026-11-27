namespace DbClone.UI.Services.GraphLayout;

/// <summary>One connected component's cards in its own coordinates, its bounding box starting at the origin.</summary>
/// <param name="Nodes">The component's node indexes.</param>
/// <param name="TopLefts">Each card's top-left corner, parallel to <paramref name="Nodes"/>.</param>
/// <param name="Width">The component's box width.</param>
/// <param name="Height">The component's box height.</param>
internal sealed record GraphLocalLayout(IReadOnlyList<int> Nodes, IReadOnlyList<GraphPoint> TopLefts, double Width, double Height)
{
    /// <summary>Builds a local layout from card centres, moving the box to the origin.</summary>
    /// <param name="nodes">The component's node indexes.</param>
    /// <param name="centreX">Each card's centre x, parallel to the nodes.</param>
    /// <param name="centreY">Each card's centre y, parallel to the nodes.</param>
    /// <param name="widths">The card widths by node index.</param>
    /// <returns>The local layout.</returns>
    public static GraphLocalLayout FromCentres(IReadOnlyList<int> nodes, IReadOnlyList<double> centreX, IReadOnlyList<double> centreY, IReadOnlyList<double> widths)
    {
        var left = double.MaxValue;
        var top = double.MaxValue;
        var right = double.MinValue;
        var bottom = double.MinValue;

        for (var at = 0; at < nodes.Count; at++)
        {
            var half = widths[nodes[at]] / 2;
            left = Math.Min(left, centreX[at] - half);
            right = Math.Max(right, centreX[at] + half);
            top = Math.Min(top, centreY[at] - (GraphCard.Height / 2));
            bottom = Math.Max(bottom, centreY[at] + (GraphCard.Height / 2));
        }

        var corners = nodes
            .Select((node, at) => new GraphPoint(centreX[at] - (widths[node] / 2) - left, centreY[at] - (GraphCard.Height / 2) - top))
            .ToArray();

        return new GraphLocalLayout(nodes, corners, right - left, bottom - top);
    }
}
