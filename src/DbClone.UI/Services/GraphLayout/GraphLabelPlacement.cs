namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Where on its own route each relation states the columns it joins. The arrows are placed by the picture's
/// rules — ports, lanes, crossings — and the words come after them, because a label is the only place a reader
/// learns which two columns a line answers for. Two labels printed on one place are one relation the picture
/// hides, which is why the place is chosen from the ink the text actually needs.
/// </summary>
/// <remarks>
/// <para>
/// A label is never moved off its route, and never hidden: the route is the arrow's answer to the cards and
/// strokes around it, and this class re-picks the share of it the text stands at and nothing else. Labels are
/// chosen in the sets a reader reveals them in — clicking a table speaks for every relation that meets it, and
/// the constraints that join one pair of tables are drawn as lanes side by side — so a set is settled against
/// the ink of every set placed before it.
/// </para>
/// <para>
/// A family is offered one share for all its lanes first, because lanes dealt a port step apart keep that
/// distance wherever along them the text stands: printed at the same share, the labels sit a line below one
/// another and read as the stack of constraints they are. Staggering them along the route, which is what a
/// midpoint-per-lane rule does, spends the perpendicular clearance on a tilted line — on the reported
/// corridor of two lanes 18 px apart and 200 px long, carrying text 150 px wide, the stagger left them 7 px
/// apart. The stagger is kept as the answer for lanes that converge, where no common share separates them.
/// </para>
/// <para>
/// Nothing here changes the arrangement: no port, no route and no crossing moves, so the picture a reader was
/// shown is the picture they get, only legible.
/// </para>
/// </remarks>
internal static class GraphLabelPlacement
{
    /// <summary>The shares of a route a label is offered, the middle first, then a step out either side.</summary>
    private static readonly double[] MiddleOut = [0.5, 0.4, 0.6, 0.32, 0.68, 0.25, 0.75, 0.18, 0.82];

    /// <summary>The share of its route a label is never pushed past, so a word never trades places with an arrow head.</summary>
    private const double EdgeShare = 0.1;

    /// <summary>Printing two revealed labels on one place, which no readability term can outweigh.</summary>
    private const int Overprint = 1_000_000;

    /// <summary>Two labels the reader uncovers together only by pointing at an arrow while a table is selected.</summary>
    private const int Distant = 10;

    /// <summary>A label whose ink shares the page with a card.</summary>
    private const int OverCard = 5;

    /// <summary>Re-reading a route for the label's sake, for each whole share of it moved.</summary>
    private const int Churn = 20;

    /// <summary>The page two labels may share before it costs a point, so a set is spread rather than merely shifted.</summary>
    private const double SharedPage = 20;

    private static readonly GraphLayoutValidator Validator = new();

    /// <summary>Places every label so the picture can be read.</summary>
    /// <param name="nodes">The cards, by node index.</param>
    /// <param name="routes">The routed relations, in edge-index order.</param>
    /// <param name="edges">The relations as child and parent node indexes, in edge-index order.</param>
    /// <param name="labels">The text each relation prints, in edge-index order.</param>
    /// <returns>The same routes, each carrying the label point its text reads best at.</returns>
    internal static IReadOnlyList<EdgeRoute> Place(
        IReadOnlyList<NodePlacement> nodes,
        IReadOnlyList<EdgeRoute> routes,
        IReadOnlyList<(int Child, int Parent)> edges,
        IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(labels);

        var family = GraphNonOrthogonalRouter.Parallel(edges);
        var answer = routes.ToArray();
        var printed = new List<GraphLabelInk>(routes.Count);

        foreach (var group in Groups(Lanes(routes, edges, labels), family))
        {
            foreach (var (lane, share) in group.Zip(Settle(group, printed, nodes)))
            {
                var anchor = GraphGeometry.ArcPoint(lane.Line, share);
                answer[lane.Route] = routes[lane.Route] with { LabelPoint = anchor };
                printed.Add(GraphLabelInk.Around(anchor, lane.Text, lane.Child, lane.Parent));
            }
        }

        return answer;
    }

    /// <summary>
    /// The sets whose labels have to be chosen together: every constraint joining one pair of tables, because
    /// a reader clicking either of them reveals the set at once and its lanes run side by side. A relation that
    /// joins its pair alone is a set of one. The sets are settled in the order of the room they have rather than
    /// the order they are drawn in — see <see cref="Reach"/> — and each is measured against the ink of every set
    /// placed before it, so no pair is weighed twice.
    /// </summary>
    private static IEnumerable<Lane[]> Groups(IReadOnlyList<Lane> lanes, (int Ordinal, int Count)[] family)
    {
        var pairs = new Dictionary<(int, int), List<Lane>>();
        var groups = new List<List<Lane>>();

        foreach (var lane in lanes)
        {
            if (family[lane.Edge].Count <= 1)
            {
                groups.Add([lane]);
                continue;
            }

            var key = (Math.Min(lane.Child, lane.Parent), Math.Max(lane.Child, lane.Parent));
            if (!pairs.TryGetValue(key, out var members))
            {
                pairs[key] = members = [];
                groups.Add(members);
            }

            members.Add(lane);
        }

        return groups
            .OrderBy(group => Reach(group))
            .ThenBy(group => group.Min(lane => lane.Route))
            .Select(group => group.OrderBy(lane => lane.Route).ToArray());
    }

    /// <summary>
    /// How far the most tightly kept label of a set can step away from where it stands. It is the reach of the
    /// line and not its length: a self-reference travels eighty pixels around an eighteen pixel column and never
    /// leaves it, while a two hundred pixel corridor can carry its word out of the way of anything beside it.
    /// A set with no reach cannot make room for another, so it has to be placed first and have room made for it;
    /// settled the other way round it is left printed over, which is what the reader cannot get back.
    /// </summary>
    private static double Reach(IReadOnlyList<Lane> group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return group.Min(static lane =>
        {
            var (left, right, top, bottom) = (double.MaxValue, double.MinValue, double.MaxValue, double.MinValue);
            foreach (var point in lane.Line)
            {
                left = Math.Min(left, point.X);
                right = Math.Max(right, point.X);
                top = Math.Min(top, point.Y);
                bottom = Math.Max(bottom, point.Y);
            }

            return Math.Max(right - left, bottom - top);
        });
    }

    /// <summary>Each relation reduced to what a label is chosen from: its own line, its text and the pair it joins.</summary>
    private static IReadOnlyList<Lane> Lanes(
        IReadOnlyList<EdgeRoute> routes,
        IReadOnlyList<(int Child, int Parent)> edges,
        IReadOnlyList<string> labels)
    {
        var lanes = new List<Lane>(routes.Count);

        for (var route = 0; route < routes.Count; route++)
        {
            var edge = routes[route].EdgeIndex;
            if (edge < 0 || edge >= edges.Count || edge >= labels.Count)
            {
                continue;
            }

            var line = LineOf(routes[route]);
            var length = line is null ? 0 : GraphGeometry.ArcLength(line);
            if (line is null || length <= 0)
            {
                // A route with no line to read has no place to state its columns either; it keeps the one it was
                // handed with rather than being moved onto a coordinate that answers to nothing.
                continue;
            }

            lanes.Add(new Lane(route, edge, edges[edge].Child, edges[edge].Parent, line, length, labels[edge], ShareOf(line, routes[route].LabelPoint)));
        }

        return lanes;
    }

    /// <summary>The route as the browser paints it, which is what a label has to clear.</summary>
    private static IReadOnlyList<GraphPoint>? LineOf(EdgeRoute route) =>
        route.Geometry == EEdgeGeometry.Straight ? route.Points : Validator.Flatten(route)?.Points;

    /// <summary>
    /// The shares the set may take, in the order they are wanted: the place every one of them was handed with,
    /// then all of them at one share of their own routes, then staggered along them by as much as the widest
    /// text needs. The middle is offered before the ends because a label reads as belonging to its arrow where
    /// it crosses it.
    /// </summary>
    private static IEnumerable<double[]> Candidates(Lane[] group)
    {
        yield return group.Select(lane => lane.Kept).ToArray();

        var step = group.Length == 1 ? 0 : Step(group);
        var middle = (group.Length + 1) / 2.0;

        foreach (var share in MiddleOut)
        {
            yield return group.Select(_ => share).ToArray();

            if (step > 0)
            {
                yield return group
                    .Select((_, at) => Math.Clamp(share + (((at + 1) - middle) * step), EdgeShare, 1 - EdgeShare))
                    .ToArray();
            }
        }
    }

    /// <summary>How far apart, in shares, two labels of a set must stand to clear each other along their routes.</summary>
    private static double Step(Lane[] group)
    {
        var widest = group.Max(lane => GraphLabelInk.WidthOf(lane.Text)) + (2 * GraphLabelInk.InkGap);
        var shortest = group.Min(lane => lane.Length);

        return Math.Min(0.34, widest / shortest);
    }

    /// <summary>The shares the set is placed at: the first candidate nothing of the picture objects to.</summary>
    private static double[] Settle(Lane[] group, IReadOnlyList<GraphLabelInk> printed, IReadOnlyList<NodePlacement> nodes)
    {
        var best = group.Select(lane => lane.Kept).ToArray();
        var cheapest = int.MaxValue;

        foreach (var shares in Candidates(group))
        {
            var cost = Cost(group, shares, printed, nodes);
            if (cost >= cheapest)
            {
                continue;
            }

            cheapest = cost;
            best = shares;
            if (cost == 0)
            {
                break;
            }
        }

        return best;
    }

    /// <summary>What the picture would lose if the set stood at these shares: ink printed on ink, then ink on a card, then churn.</summary>
    private static int Cost(Lane[] group, double[] shares, IReadOnlyList<GraphLabelInk> printed, IReadOnlyList<NodePlacement> nodes)
    {
        var ink = new GraphLabelInk[group.Length];
        for (var at = 0; at < group.Length; at++)
        {
            ink[at] = GraphLabelInk.Around(
                GraphGeometry.ArcPoint(group[at].Line, shares[at]),
                group[at].Text,
                group[at].Child,
                group[at].Parent);
        }

        var cost = 0;
        for (var at = 0; at < ink.Length; at++)
        {
            cost += (int)Math.Round(Math.Abs(shares[at] - group[at].Kept) * Churn);

            for (var other = at + 1; other < ink.Length; other++)
            {
                cost += Penalty(ink[at], ink[other], Overprint);
            }

            foreach (var before in printed)
            {
                cost += Penalty(ink[at], before, ink[at].RevealedWith(before) ? Overprint : Distant);
            }

            cost += nodes.Count(card => ink[at].LiesOver(card)) * OverCard;
        }

        return cost;
    }

    /// <summary>
    /// What two labels standing here cost the reader: that they cannot be read apart, and — besides that —
    /// how much page they still share, so a set that has no place free of one another is spread as far as its
    /// routes reach instead of being left where it was.
    /// </summary>
    private static int Penalty(GraphLabelInk ink, GraphLabelInk other, int overprint) =>
        ink.Meets(other) ? overprint + ((int)Math.Round(ink.Overlap(other) / SharedPage)) : 0;

    /// <summary>The share of a line its label point stands at, which is the place the route was handed with.</summary>
    private static double ShareOf(IReadOnlyList<GraphPoint> line, GraphPoint anchor)
    {
        var total = GraphGeometry.ArcLength(line);
        if (total <= 0)
        {
            return 0.5;
        }

        var best = 0.5;
        var walked = 0.0;
        var nearest = double.MaxValue;

        for (var step = 1; step < line.Count; step++)
        {
            var (from, to) = (line[step - 1], line[step]);
            var length = GraphGeometry.Distance(from, to);
            var along = length == 0
                ? 0
                : Math.Clamp(
                    (((anchor.X - from.X) * (to.X - from.X)) + ((anchor.Y - from.Y) * (to.Y - from.Y))) / (length * length),
                    0,
                    1);
            var projection = new GraphPoint(from.X + (along * (to.X - from.X)), from.Y + (along * (to.Y - from.Y)));
            var distance = GraphGeometry.Distance(projection, anchor);

            if (distance < nearest)
            {
                nearest = distance;
                best = (walked + (along * length)) / total;
            }

            walked += length;
        }

        return best;
    }

    /// <summary>One relation a label is chosen for.</summary>
    /// <param name="Route">Where the relation sits in the routes it was placed among.</param>
    /// <param name="Edge">The relation's index.</param>
    /// <param name="Child">The table the relation leaves.</param>
    /// <param name="Parent">The table it names.</param>
    /// <param name="Line">The route as the browser paints it.</param>
    /// <param name="Length">How long that line is.</param>
    /// <param name="Text">The columns the relation states.</param>
    /// <param name="Kept">The share of the line the label was handed with.</param>
    private sealed record Lane(
        int Route,
        int Edge,
        int Child,
        int Parent,
        IReadOnlyList<GraphPoint> Line,
        double Length,
        string Text,
        double Kept);
}
