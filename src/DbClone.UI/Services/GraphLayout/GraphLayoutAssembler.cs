namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Joins the layouts of the connected components into one. Each component was routed in its own frame, so
/// its box is the bounding box of its cards and its routes together; the boxes are packed with the
/// component gap, and nothing of one component can then reach another's cards or strokes.
/// </summary>
internal static class GraphLayoutAssembler
{
    /// <summary>Assembles the component layouts.</summary>
    /// <param name="profile">The whole graph.</param>
    /// <param name="parts">Each component with its valid layout, in component order.</param>
    /// <param name="name">The name of the assembled layout.</param>
    /// <param name="evaluations">The evaluations spent over all components.</param>
    /// <returns>The layout of the whole graph; routes and ports in whole-graph edge order.</returns>
    public static GraphLayoutCandidate Assemble(
        GraphLayoutProfile profile,
        IReadOnlyList<(GraphSubgraph Sub, GraphLayoutCandidate Layout)> parts,
        string name,
        int evaluations)
    {
        var validator = new GraphLayoutValidator();
        var boxes = parts.Select(part => Box(part.Layout, validator)).ToArray();
        var (offsets, width, height) = GraphComponentPacker.PackBoxes(boxes.Select(box => (box.Right - box.Left, box.Bottom - box.Top)).ToArray());

        var nodes = new NodePlacement[profile.NodeCount];
        var ends = new GraphEdgeEnds[profile.Edges.Count];
        var routes = new EdgeRoute[profile.Edges.Count];

        for (var part = 0; part < parts.Count; part++)
        {
            var (sub, layout) = parts[part];
            var (dx, dy) = (offsets[part].X - boxes[part].Left, offsets[part].Y - boxes[part].Top);

            foreach (var card in layout.Layout.Nodes)
            {
                nodes[sub.Nodes[card.NodeIndex]] = new NodePlacement(
                    sub.Nodes[card.NodeIndex],
                    new GraphPoint(card.TopLeft.X + dx, card.TopLeft.Y + dy),
                    card.Width,
                    card.Height);
            }

            for (var local = 0; local < sub.Edges.Count; local++)
            {
                var edge = sub.Edges[local];
                ends[edge] = new GraphEdgeEnds(Shift(layout.Ends[local].Child, dx, dy), Shift(layout.Ends[local].Parent, dx, dy));
                var route = layout.Routes[local];
                routes[edge] = route with
                {
                    EdgeIndex = edge,
                    Points = route.Points.Select(point => new GraphPoint(point.X + dx, point.Y + dy)).ToArray(),
                    LabelPoint = new GraphPoint(route.LabelPoint.X + dx, route.LabelPoint.Y + dy),
                };
            }
        }

        var validity = validator.Validate(nodes, routes, profile.Edges.Select(edge => new GraphEdgeEndpoints(edge.Child, edge.Parent)).ToArray());

        // Every part was valid in its own frame and the packing only translates, so an invalid assembly is
        // a defect here rather than a picture: it is refused instead of being handed on as a valid layout.
        if (!validity.IsValid)
        {
            throw new InvalidOperationException(
                "The assembled layout is not valid: "
                + string.Join(", ", validity.Failures.Select(failure => $"{failure.EdgeIndex}:{failure.Kind}")));
        }

        return new GraphLayoutCandidate(
            name,
            new GraphDraftLayout(name, nodes, width, height),
            ends,
            routes,
            validity.Crossings,
            evaluations,
            parts.Sum(part => part.Layout.Moves),
            parts.Sum(part => part.Layout.Expansions));
    }

    private static GraphEdgePort Shift(GraphEdgePort port, double dx, double dy) => port with { Point = new GraphPoint(port.Point.X + dx, port.Point.Y + dy) };

    /// <summary>The bounding box of a component's cards and flattened routes.</summary>
    private static (double Left, double Top, double Right, double Bottom) Box(GraphLayoutCandidate layout, GraphLayoutValidator validator)
    {
        var points = layout.Layout.Nodes.SelectMany(card => new[] { card.TopLeft, new GraphPoint(card.Right, card.Bottom) })
            .Concat(layout.Routes.SelectMany(route => validator.Flatten(route)!.Points))
            .ToArray();

        return (points.Min(point => point.X), points.Min(point => point.Y), points.Max(point => point.X), points.Max(point => point.Y));
    }
}
