using System.Text;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services.GraphAnalysis;
using DbClone.UI.Services.GraphLayout;

namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// Everything inside the viewport: one routed path per constraint with the columns it joins written
/// beside it and hidden, one card per table, and the supernode that stands over a collapsed cyclic
/// group.
/// </summary>
/// <remarks>
/// <para>
/// This writer makes no layout decision. A route arrives as points and a card arrives as a placed
/// rectangle with the two lines it prints already measured.
/// </para>
/// <para>
/// The columns a constraint joins are never printed on the picture. They are written into the document
/// and hidden by the <c>quiet</c> class, so pointing at an arrow or selecting it names the columns while
/// the picture itself stays free of text in its corridors (D4, T22).
/// </para>
/// <para>
/// A collapsed group's members are written and then hidden by class, never omitted: switching a chip
/// or expanding a supernode changes what is displayed, never what the document knows (FR16, D7).
/// </para>
/// <para>
/// An arrow is written with the <c>away</c> class when one of the two tables it joins is not drawn:
/// a picture that opens on one neighbourhood would otherwise run its lines to positions where no card
/// stands, and an arrow into blank space reads as a relation to nothing. The arrow is still in the
/// document, so revealing its table shows it without a re-render — the same rule the chips follow,
/// stated once and applied to the opening view as well (D8a).
/// </para>
/// </remarks>
internal sealed class GraphPictureWriter
{
    private readonly CatalogGraphModel _model;
    private readonly CatalogGraphLayout _layout;
    private readonly ClusterSwitchSet _switches;
    private readonly HashSet<int> _drawn;

    /// <summary>Creates the writer for one document.</summary>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="layout">The computed coordinates.</param>
    public GraphPictureWriter(CatalogGraphModel model, CatalogGraphLayout layout)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(layout);

        _model = model;
        _layout = layout;
        _switches = new ClusterSwitchSet(model);
        _drawn = Drawn();
    }

    /// <summary>Writes the viewport's contents: one path per constraint, one card per table.</summary>
    /// <param name="html">The document being built.</param>
    public void WriteTo(StringBuilder html)
    {
        ArgumentNullException.ThrowIfNull(html);

        // The marks live in the document's own defs, outside this group, so the key under the header can
        // reference the shapes the arrows carry instead of describing them (GraphMarkerDefs).
        html.Append("<g id=\"viewport\">\n");

        WriteEdges(html);
        WriteNodes(html);
        WriteSupernodes(html);

        html.Append("</g>\n");
    }

    private void WriteEdges(StringBuilder html)
    {
        html.Append("<g class=\"edges\">\n");
        foreach (var edge in _model.Edges)
        {
            var route = _layout.Edges[edge.Index];
            var away = _drawn.Contains(edge.ChildIndex) && _drawn.Contains(edge.ParentIndex) ? string.Empty : " away";

            // The bar is the only mark a child end gets, and only where the catalog proves the pair
            // one-to-one: a plain arrow already means many rows may name one, so marking that case
            // would repeat the definition of a foreign key on every line in the picture (D14.1).
            var bar = edge.IsOneToOne ? " marker-start=\"url(#one)\"" : string.Empty;

            html.Append("<path class=\"edge").Append(edge.ParentIsCaptured ? string.Empty : " out").Append(away)
                .Append("\" data-e=\"").Append(edge.Index).Append("\" d=\"").Append(EdgePath(route))
                // The mark belongs to the painted line and to nothing else: the band that takes the
                // pointer repeats the same geometry, so a mark on it would double every folded arrow.
                .Append(bar)
                .Append("\" marker-end=\"url(#head)\"><title>").Append(GraphHtmlText.Escape(edge.Label))
                .Append("</title></path>\n");

            // A 1.3 px line is a hard thing to point at, so a second path with the same geometry lies over
            // it, stroked wide and transparent: the arrow can be pointed at and selected from a comfortable
            // distance. It carries the same data-e as the line it covers, so the script's highlight, the
            // direction filter and the click handler treat the two as one arrow, and it sits immediately
            // before the label so the label's CSS reveal follows the band the reader actually points at
            // (FR13). The cards are painted after the edges, so a band that passes under a card never takes
            // the card's own click.
            html.Append("<path class=\"edge pick").Append(away).Append("\" data-e=\"").Append(edge.Index).Append("\" d=\"")
                .Append(EdgePath(route)).Append("\"><title>")
                .Append(GraphHtmlText.Escape(edge.Label)).Append("</title></path>\n");

            // Every label is written and hidden: text on the line is unreadable wherever the corridor
            // also carries the stroke, and a label that appears only where it fits teaches the reader
            // to distrust the picture's silence. The stylesheet reveals this one on hover or selection.
            html.Append("<text class=\"elabel quiet\" x=\"").Append(GraphHtmlText.Num(route.LabelPoint.X))
                .Append("\" y=\"").Append(GraphHtmlText.Num(route.LabelPoint.Y))
                .Append("\">").Append(GraphHtmlText.Escape(edge.PictureLabel)).Append("</text>\n");
        }

        html.Append("</g>\n");
    }

    /// <summary>
    /// One path per constraint: a straight segment, the dog-leg the layout turned in a corridor, or
    /// the cubic that carries a cycle or a loop. A three-constraint pair stays three paths with three
    /// ids (FR13).
    /// </summary>
    private static string EdgePath(EdgeRoute route)
    {
        var points = route.Points;
        if (route.Shape == EEdgeShape.Line || points.Count < 4)
        {
            var path = new StringBuilder();
            path.Append("M ").Append(GraphHtmlText.Num(points[0].X)).Append(' ').Append(GraphHtmlText.Num(points[0].Y));

            for (var index = 1; index < points.Count; index++)
            {
                path.Append(" L ").Append(GraphHtmlText.Num(points[index].X)).Append(' ').Append(GraphHtmlText.Num(points[index].Y));
            }

            return path.ToString();
        }

        return $"M {GraphHtmlText.Num(points[0].X)} {GraphHtmlText.Num(points[0].Y)} " +
            $"C {GraphHtmlText.Num(points[1].X)} {GraphHtmlText.Num(points[1].Y)} " +
            $"{GraphHtmlText.Num(points[2].X)} {GraphHtmlText.Num(points[2].Y)} " +
            $"{GraphHtmlText.Num(points[3].X)} {GraphHtmlText.Num(points[3].Y)}";
    }

    private void WriteNodes(StringBuilder html)
    {
        html.Append("<g class=\"nodes\">\n");

        foreach (var node in _model.Nodes)
        {
            var place = _layout.Nodes[node.Index];
            var hidden = IsCollapsedMember(node);
            html.Append("<g class=\"node").Append(node.IsCaptured ? " cap" : " out");
            if (hidden)
            {
                html.Append(" member hidden");
            }
            else if (!_drawn.Contains(node.Index))
            {
                html.Append(" away");
            }

            html.Append("\" data-n=\"").Append(node.Index).Append("\" data-c=\"")
                .Append(Claims(node.Index)).Append('"');
            if (hidden)
            {
                html.Append(" data-m=\"").Append(node.CyclicGroupNumber).Append('"');
            }

            html.Append(">\n<title>").Append(GraphHtmlText.Escape(GraphFactText.NodeTitle(node)))
                .Append("</title>\n");

            html.Append("<rect x=\"").Append(GraphHtmlText.Num(place.TopLeft.X))
                .Append("\" y=\"").Append(GraphHtmlText.Num(place.TopLeft.Y))
                .Append("\" width=\"").Append(GraphHtmlText.Num(place.Width))
                .Append("\" height=\"").Append(GraphHtmlText.Num(place.Height)).Append("\" rx=\"7\"/>\n");

            var textX = place.TopLeft.X + GraphCard.Padding;
            html.Append("<text class=\"name\" x=\"").Append(GraphHtmlText.Num(textX))
                .Append("\" y=\"").Append(GraphHtmlText.Num(place.TopLeft.Y + GraphCard.NameBaseline)).Append("\">")
                .Append(GraphHtmlText.Escape(node.DisplayName)).Append("</text>\n");
            html.Append("<text class=\"count\" x=\"").Append(GraphHtmlText.Num(textX))
                .Append("\" y=\"").Append(GraphHtmlText.Num(place.TopLeft.Y + GraphCard.FactBaseline)).Append("\">")
                .Append(GraphHtmlText.Escape(GraphFactText.NodeFacts(node))).Append("</text>\n");

            html.Append("</g>\n");
        }

        html.Append("</g>\n");
    }

    private bool IsCollapsedMember(CatalogGraphNode node)
    {
        if (node.CyclicGroupNumber is not { } number)
        {
            return false;
        }

        return _model.CyclicGroups.Any(group => group.Number == number && group.IsCollapsed);
    }

    /// <summary>
    /// The tables the picture puts on screen when it opens: the opening view's own tables, plus the
    /// members of a collapsed group, which the supernode standing over them draws in their place.
    /// </summary>
    private HashSet<int> Drawn()
    {
        var drawn = new HashSet<int>(_model.InitialView.VisibleNodeIndexes);
        foreach (var node in _model.Nodes)
        {
            if (IsCollapsedMember(node))
            {
                drawn.Add(node.Index);
            }
        }

        return drawn;
    }

    /// <summary>
    /// One supernode per collapsed group, drawn over its hidden members with the group's own label as
    /// the expand affordance (FR16).
    /// </summary>
    private void WriteSupernodes(StringBuilder html)
    {
        foreach (var group in _model.CyclicGroups)
        {
            if (!group.IsCollapsed || group.MemberNodeIndexes.Count == 0)
            {
                continue;
            }

            var boxes = group.MemberNodeIndexes.Select(node => _layout.Nodes[node]).ToArray();
            var left = boxes.Min(box => box.TopLeft.X);
            var top = boxes.Min(box => box.TopLeft.Y);
            var right = boxes.Max(box => box.Right);
            var bottom = boxes.Max(box => box.Bottom);

            html.Append("<g class=\"super\" data-g=\"").Append(group.Number).Append("\" role=\"button\" tabindex=\"0\">\n");
            html.Append("<title>").Append(GraphHtmlText.Escape(group.Label)).Append(" \u2014 select to expand ")
                .Append(group.MemberNodeIndexes.Count).Append(" tables</title>\n");
            html.Append("<rect x=\"").Append(GraphHtmlText.Num(left - 6)).Append("\" y=\"").Append(GraphHtmlText.Num(top - 6))
                .Append("\" width=\"").Append(GraphHtmlText.Num(right - left + 12))
                .Append("\" height=\"").Append(GraphHtmlText.Num(bottom - top + 12)).Append("\" rx=\"10\"/>\n");
            html.Append("<text x=\"").Append(GraphHtmlText.Num((left + right) / 2))
                .Append("\" y=\"").Append(GraphHtmlText.Num((top + bottom) / 2 + 5))
                .Append("\" text-anchor=\"middle\">").Append(GraphHtmlText.Escape(group.Label)).Append("</text>\n");
            html.Append("</g>\n");
        }
    }

    private string Claims(int nodeIndex) =>
        _model.ChipIndexesByNode.TryGetValue(nodeIndex, out var chips)
            ? string.Join(' ', _switches.Switchable(chips))
            : string.Empty;
}
