namespace DbClone.UI.Services.GraphLayout;

/// <summary>Packs independent component drawings into one canvas, left to right and wrapping, with the component gap.</summary>
internal static class GraphComponentPacker
{
    /// <summary>Packs the components.</summary>
    /// <param name="name">The seed's name.</param>
    /// <param name="profile">The graph facts, for the card widths.</param>
    /// <param name="locals">The components' local layouts.</param>
    /// <returns>The draft with absolute placements.</returns>
    public static GraphDraftLayout Pack(string name, GraphLayoutProfile profile, IReadOnlyList<GraphLocalLayout> locals) =>
        Pack(name, profile.Widths, locals);

    /// <summary>Packs the components with explicit card widths, which repair may have widened.</summary>
    /// <param name="name">The seed's name.</param>
    /// <param name="widths">The card width of every node.</param>
    /// <param name="locals">The components' local layouts.</param>
    /// <returns>The draft with absolute placements.</returns>
    public static GraphDraftLayout Pack(string name, IReadOnlyList<double> widths, IReadOnlyList<GraphLocalLayout> locals)
    {
        var ordered = locals.OrderBy(local => local.Nodes[0]).ToArray();
        var (offsets, width, height) = PackBoxes(ordered.Select(local => (local.Width, local.Height)).ToArray());
        var placed = new NodePlacement[widths.Count];

        for (var box = 0; box < ordered.Length; box++)
        {
            var local = ordered[box];
            for (var at = 0; at < local.Nodes.Count; at++)
            {
                var node = local.Nodes[at];
                placed[node] = new NodePlacement(
                    node,
                    new GraphPoint(offsets[box].X + local.TopLefts[at].X, offsets[box].Y + local.TopLefts[at].Y),
                    widths[node],
                    GraphCard.Height);
            }
        }

        return new GraphDraftLayout(name, placed, width, height);
    }

    /// <summary>Shelf-packs boxes in the order given, wrapping so the canvas stays wider than tall.</summary>
    /// <param name="boxes">The box sizes.</param>
    /// <returns>The top-left corner of every box and the canvas size, margins included.</returns>
    public static (IReadOnlyList<GraphPoint> Offsets, double Width, double Height) PackBoxes(IReadOnlyList<(double Width, double Height)> boxes)
    {
        var margin = GraphPlacementConstants.Margin;
        var gap = GraphPlacementConstants.ComponentGap;
        var area = boxes.Sum(box => (box.Width + gap) * (box.Height + gap));
        var target = Math.Max(boxes.Count == 0 ? 0 : boxes.Max(box => box.Width), Math.Sqrt(area * GraphPlacementConstants.Aspect));

        var offsets = new GraphPoint[boxes.Count];
        double x = margin, y = margin, rowHeight = 0, widest = 0;

        for (var at = 0; at < boxes.Count; at++)
        {
            if (x > margin && x - margin + boxes[at].Width > target)
            {
                x = margin;
                y += rowHeight + gap;
                rowHeight = 0;
            }

            offsets[at] = new GraphPoint(x, y);
            x += boxes[at].Width + gap;
            rowHeight = Math.Max(rowHeight, boxes[at].Height);
            widest = Math.Max(widest, x - gap);
        }

        return (offsets, widest + margin, y + rowHeight + margin);
    }
}
