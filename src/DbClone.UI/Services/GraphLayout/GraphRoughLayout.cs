namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The arrangement of last resort: every card placed and every relation drawn from one border port to the
/// other, with no claim that a stroke clears a card or another stroke. It exists because a picture the reader
/// can look at and judge is worth more than a window that shows nothing — the owner's rule for the watchdog,
/// applied to the case where no candidate passed the hard rules at all.
/// </summary>
/// <remarks>
/// The ports come from the same assigner every other arrangement uses, so two ends on one border still take
/// two places and two arrow heads never print as one mark. What the arrangement does not promise is that a
/// stroke passes around the tables in its way; the caller says so beside the picture.
/// </remarks>
internal static class GraphRoughLayout
{
    /// <summary>Draws the whole graph as one straight stretch per relation, in the ranked arrangement.</summary>
    /// <param name="profile">The graph facts.</param>
    /// <param name="name">The name the arrangement reports.</param>
    /// <returns>A complete arrangement: one card per node, one route per relation.</returns>
    public static GraphLayoutCandidate Of(GraphLayoutProfile profile, string name = "rough") =>
        Of(profile, GraphRankedSeed.Place(profile), name);

    /// <summary>
    /// Draws the whole graph as one straight stretch per relation, in the shape of the arrangement it is given.
    /// The search hands it the seed that came nearest, so the picture the reader judges keeps the shape the
    /// search believed in most — a ring around the busiest table stays a ring — instead of being redrawn flat.
    /// </summary>
    /// <param name="profile">The graph facts.</param>
    /// <param name="draft">The arrangement to draw the cards in.</param>
    /// <param name="name">The name the arrangement reports; the seed's own name is appended to it.</param>
    /// <returns>A complete arrangement: one card per node, one route per relation.</returns>
    public static GraphLayoutCandidate Of(GraphLayoutProfile profile, GraphDraftLayout draft, string name = "rough")
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(draft);

        var reported = $"{name}-{draft.Name}";
        var layout = GraphPlacementState.From(draft, profile).Assemble(reported);
        var ports = GraphPortAssigner.Assign(layout.Nodes, profile.Edges);
        var ends = new GraphEdgeEnds[profile.Edges.Count];
        var routes = new EdgeRoute[profile.Edges.Count];

        for (var edge = 0; edge < profile.Edges.Count; edge++)
        {
            var (child, parent) = profile.Edges[edge];

            // A border that could not hold its demand still gets a point on the line between the two cards:
            // the picture keeps its arrow rather than losing the relation it stands for.
            var pair = ports.Ends[edge]
                ?? new GraphEdgeEnds(
                    BorderPoint(layout.Nodes[child], layout.Nodes[parent]),
                    BorderPoint(layout.Nodes[parent], layout.Nodes[child]));

            ends[edge] = pair;
            routes[edge] = new EdgeRoute(
                edge,
                EEdgeShape.Line,
                EEdgeGeometry.Straight,
                [pair.Child.Point, pair.Parent.Point],
                new GraphPoint(
                    (pair.Child.Point.X + pair.Parent.Point.X) / 2,
                    (pair.Child.Point.Y + pair.Parent.Point.Y) / 2));
        }

        // The crossings are still counted, so the caller never has to present a rough picture as a clean one.
        var validity = new GraphLayoutValidator().Validate(
            layout.Nodes,
            routes,
            profile.Edges.Select(edge => new GraphEdgeEndpoints(edge.Child, edge.Parent)).ToArray());

        return new GraphLayoutCandidate(reported, layout, ends, routes, validity.Crossings, 0, 0, 0);
    }

    /// <summary>The point where the straight way to another card leaves this card's border.</summary>
    /// <param name="card">The card the point sits on.</param>
    /// <param name="other">The card the relation leads to.</param>
    /// <returns>The border point, with the side it sits on.</returns>
    private static GraphEdgePort BorderPoint(NodePlacement card, NodePlacement other)
    {
        var (dx, dy) = (other.Centre.X - card.Centre.X, other.Centre.Y - card.Centre.Y);
        var across = dx == 0 ? double.PositiveInfinity : (card.Width / 2) / Math.Abs(dx);
        var down = dy == 0 ? double.PositiveInfinity : (card.Height / 2) / Math.Abs(dy);
        var reach = Math.Min(across, down);

        if (!double.IsFinite(reach))
        {
            return new GraphEdgePort(EEdgeSide.Right, card.Centre);
        }

        var point = new GraphPoint(
            card.Centre.X + (dx * reach),
            card.Centre.Y + (dy * reach));

        return new GraphEdgePort(
            across <= down ? (dx > 0 ? EEdgeSide.Right : EEdgeSide.Left) : (dy > 0 ? EEdgeSide.Bottom : EEdgeSide.Top),
            point);
    }
}
