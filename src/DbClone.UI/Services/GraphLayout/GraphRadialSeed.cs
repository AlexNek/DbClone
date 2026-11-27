namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The radial seed: each component's core — its largest cyclic group, or else its best-connected table —
/// in the middle, and every other table on a ring at its distance from that core.
/// </summary>
internal static class GraphRadialSeed
{
    /// <summary>Places every component.</summary>
    /// <param name="profile">The graph facts.</param>
    /// <param name="hub">An explicit topology-selected hub; other components retain their usual core.</param>
    /// <returns>The draft.</returns>
    public static GraphDraftLayout Place(GraphLayoutProfile profile, int? hub = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return GraphComponentPacker.Pack("radial", profile, profile.ComponentNodes.Select(nodes => Component(profile, nodes, hub)).ToArray());
    }

    private static GraphLocalLayout Component(GraphLayoutProfile profile, IReadOnlyList<int> nodes, int? hub)
    {
        var core = hub is { } root && nodes.Contains(root) ? new List<int> { root } : Core(profile, nodes);
        var distance = Distances(profile, core);
        var settlement = Settlements(profile, nodes, core);
        var angleOf = new Dictionary<int, double>();
        var (x, y) = (new double[nodes.Count], new double[nodes.Count]);
        var indexOf = nodes.Select((node, at) => (node, at)).ToDictionary(pair => pair.node, pair => pair.at);
        var radius = 0.0;
        var previousWidest = 0.0;
        var rings = new Dictionary<int, (double Radius, double Step)>();

        for (var ring = 0; ring <= distance.Values.Max(); ring++)
        {
            var facing = Facing(profile, nodes, distance, ring, angleOf);
            // A central point has no direction to face. Its first ring uses the whole circle, not one semicircle.
            if (hub is { } pinned && core.Count == 1 && core[0] == pinned && ring == 1)
                foreach (var node in facing.Keys.ToArray()) facing[node] = double.MaxValue;
            var members = Ring(nodes, distance, ring, facing, settlement);
            if (members.Count == 0)
            {
                continue;
            }

            var widest = members.Max(node => profile.Widths[node]);
            var needed = members.Sum(node => profile.Widths[node] + GraphPlacementConstants.CardGap) / (2 * Math.PI);
            var step = ring == 0 ? 0 : GraphCard.Height + GraphPlacementConstants.RowGap + ((previousWidest + widest) / 4);
            radius = ring == 0 && members.Count == 1 ? 0 : Math.Max(needed, radius + step);
            rings[ring] = (radius, step);

            var angles = Spread(members, facing, radius, profile.Widths);
            for (var at = 0; at < members.Count; at++)
            {
                angleOf[members[at]] = angles[at];
                x[indexOf[members[at]]] = radius * Math.Cos(angles[at]);
                y[indexOf[members[at]]] = radius * Math.Sin(angles[at]);
            }

            previousWidest = widest;
        }

        GraphCardSeparator.Settle(nodes, x, y, profile.Widths);

        GraphLaneReservation.Reserve(
            nodes,
            x,
            y,
            profile.Widths,
            nodes.Select(node => distance[node]).ToArray(),
            rings,
            profile.Edges
                .Where(edge => edge.Child != edge.Parent && indexOf.ContainsKey(edge.Child) && indexOf.ContainsKey(edge.Parent))
                .Select(edge => (indexOf[edge.Child], indexOf[edge.Parent]))
                .ToArray());

        return GraphLocalLayout.FromCentres(nodes, x, y, profile.Widths);
    }

    private static List<int> Core(GraphLayoutProfile profile, IReadOnlyList<int> nodes)
    {
        var largest = nodes.Max(node => profile.GroupSize[node]);
        if (largest > 1)
        {
            var group = nodes.Where(node => profile.GroupSize[node] == largest).Select(node => profile.Group[node]).Min();
            return nodes.Where(node => profile.Group[node] == group).ToList();
        }

        return [nodes.OrderByDescending(node => profile.Neighbours[node].Count).ThenBy(node => node).First()];
    }

    private static Dictionary<int, int> Distances(GraphLayoutProfile profile, List<int> core)
    {
        var distance = core.ToDictionary(node => node, _ => 0);
        var queue = new Queue<int>(core);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var next in profile.Neighbours[node].Where(next => !distance.ContainsKey(next)))
            {
                distance[next] = distance[node] + 1;
                queue.Enqueue(next);
            }
        }

        return distance;
    }

    /// <summary>The mean angle of the neighbours each card of one ring hangs from: where the card faces.</summary>
    private static Dictionary<int, double> Facing(
        GraphLayoutProfile profile,
        IReadOnlyList<int> nodes,
        Dictionary<int, int> distance,
        int ring,
        Dictionary<int, double> angleOf) =>
        nodes.Where(node => distance[node] == ring).ToDictionary(
            node => node,
            node => CircularMean(profile.Neighbours[node].Where(angleOf.ContainsKey).Select(next => angleOf[next])));

    /// <summary>
    /// The ring's cards in one circular order: the mean angle of their neighbours on the ring inside decides
    /// where each card faces, and a settlement's cards share one stretch of that circle.
    /// </summary>
    private static List<int> Ring(
        IReadOnlyList<int> nodes,
        Dictionary<int, int> distance,
        int ring,
        IReadOnlyDictionary<int, double> facing,
        IReadOnlyDictionary<int, int> settlement) =>
        nodes.Where(node => distance[node] == ring)
            .OrderBy(node => facing[node])
            .ThenBy(node => node)
            // A settlement holds one stretch of the ring: the tables that relate among themselves stay together,
            // so a cluster's relations run short and inside its own stretch instead of across another cluster's.
            // The stretches themselves still follow the circle, so a card keeps facing the neighbours it hangs from.
            .GroupBy(node => settlement[node])
            .Select(group => new
            {
                At = CircularMean(group.Select(node => facing[node]).Where(angle => angle != double.MaxValue)),
                Members = group.ToList()
            })
            .OrderBy(group => group.At)
            .ThenBy(group => group.Members[0])
            .SelectMany(group => group.Members)
            .ToList();

    /// <summary>
    /// Where on its ring each card stands. A card is drawn where the neighbours it hangs from put it — the
    /// circle is not dealt out in equal shares — and the cards are then pushed apart from both ends of the
    /// stretch until no two crowd each other, exactly as a border deals its ports to the peers that stand in
    /// front of them. A ring of one card, or one with no inner neighbour to face, keeps the even spread.
    /// </summary>
    /// <param name="members">The ring's cards in circular order.</param>
    /// <param name="facing">The angle each card faces.</param>
    /// <param name="radius">The ring's radius, which turns a card's width into the arc it needs.</param>
    /// <param name="widths">The width of every card.</param>
    /// <returns>One angle per card, in <paramref name="members"/> order.</returns>
    private static double[] Spread(
        IReadOnlyList<int> members,
        IReadOnlyDictionary<int, double> facing,
        double radius,
        IReadOnlyList<double> widths)
    {
        var count = members.Count;
        var angles = new double[count];
        var wanted = members.Select(node => facing[node]).ToArray();
        if (count == 0)
        {
            return angles;
        }

        if (radius <= 0 || count == 1 || wanted.Contains(double.MaxValue))
        {
            for (var at = 0; at < count; at++)
            {
                angles[at] = -Math.PI / 2 + (count == 1 ? 0 : 2 * Math.PI * at / count);
            }

            return angles;
        }

        // The largest gap between two cards of the order is where the circle is cut open, so the stretch a
        // settlement holds is never split across the cut.
        var cut = 0;
        var widest = double.MinValue;
        for (var at = 0; at < count; at++)
        {
            var gap = Wrapped(wanted[(at + 1) % count] - wanted[at]);
            if (gap > widest)
            {
                (widest, cut) = (gap, at);
            }
        }

        var order = new int[count];
        var wantedOpen = new double[count];
        for (var at = 0; at < count; at++)
        {
            order[at] = (cut + 1 + at) % count;
            wantedOpen[at] = at == 0
                ? wanted[order[0]]
                : wantedOpen[at - 1] + Math.Max(0, Wrapped(wanted[order[at]] - wanted[order[at - 1]]));
        }

        var step = new double[count];
        for (var at = 1; at < count; at++)
        {
            step[at] = (((widths[members[order[at - 1]]] + widths[members[order[at]]]) / 2) + GraphPlacementConstants.CardGap) / radius;
        }

        var left = (double[])wantedOpen.Clone();
        for (var at = 1; at < count; at++)
        {
            left[at] = Math.Max(left[at], left[at - 1] + step[at]);
        }

        var right = (double[])wantedOpen.Clone();
        for (var at = count - 2; at >= 0; at--)
        {
            right[at] = Math.Min(right[at], right[at + 1] - step[at + 1]);
        }

        for (var at = 0; at < count; at++)
        {
            angles[order[at]] = (left[at] + right[at]) / 2;
        }

        return angles;
    }

    /// <summary>An angle difference taken the short way round the circle, in (-π, π].</summary>
    private static double Wrapped(double delta) => delta - (2 * Math.PI * Math.Round(delta / (2 * Math.PI)));

    /// <summary>The mean of angles on a circle, which is the mean of their sine and cosine rather than of their degrees.</summary>
    private static double CircularMean(IEnumerable<double> angles)
    {
        var known = angles.ToArray();
        return known.Length == 0 ? double.MaxValue : Math.Atan2(known.Sum(Math.Sin), known.Sum(Math.Cos));
    }

    /// <summary>
    /// Numbers the settlements of one component — the groups of tables that relate mostly among themselves —
    /// so every settlement takes one arc of every ring. A table joins the settlement most of its neighbours
    /// already share, but only when that settlement is a strict majority of them: a table that merely points
    /// at a hub from every corner of the catalog keeps its own settlement instead of merging the whole picture
    /// into one. Settlements are then numbered outwards from the core, neighbours first, so two settlements
    /// that are joined at all sit side by side and share a border rather than crossing each other's arcs.
    /// </summary>
    /// <param name="profile">The graph facts.</param>
    /// <param name="nodes">The component's nodes.</param>
    /// <param name="core">The core the rings are measured from.</param>
    /// <returns>The settlement number of every node.</returns>
    private static Dictionary<int, int> Settlements(GraphLayoutProfile profile, IReadOnlyList<int> nodes, List<int> core)
    {
        var home = nodes.ToDictionary(node => node, node => node);

        for (var round = 0; round < GraphSearchBudget.MaximumSettlementRounds; round++)
        {
            var changed = false;

            foreach (var node in nodes)
            {
                var neighbours = profile.Neighbours[node];
                if (neighbours.Count == 0)
                {
                    continue;
                }

                var best = home[node];
                var bestCount = neighbours.Count(next => home[next] == best);

                foreach (var group in neighbours.GroupBy(next => home[next]).OrderBy(group => group.Key))
                {
                    if (group.Count() > bestCount && group.Count() * 2 > neighbours.Count)
                    {
                        best = group.Key;
                        bestCount = group.Count();
                    }
                }

                if (best != home[node])
                {
                    home[node] = best;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        var joined = home.Values.Distinct().ToDictionary(label => label, _ => new Dictionary<int, int>());
        foreach (var node in nodes)
        {
            foreach (var next in profile.Neighbours[node].Where(next => home[next] != home[node]))
            {
                var here = home[node];
                var there = home[next];
                joined[here][there] = joined[here].GetValueOrDefault(there) + 1;
                joined[there][here] = joined[there].GetValueOrDefault(here) + 1;
            }
        }

        var root = home[core[0]];
        var order = new List<int> { root };
        var seen = new HashSet<int> { root };
        for (var at = 0; at < order.Count; at++)
        {
            // The loudest connection to a settlement already numbered goes next, so settlements that belong
            // together end up adjacent on the ring.
            foreach (var next in joined[order[at]].OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Select(pair => pair.Key))
            {
                if (seen.Add(next))
                {
                    order.Add(next);
                }
            }
        }

        // A settlement the walk could not reach is numbered after the rest, by its lowest table.
        foreach (var label in home.Values.Distinct().OrderBy(label => label))
        {
            if (seen.Add(label))
            {
                order.Add(label);
            }
        }

        var ordinal = order.Select((label, at) => (label, at)).ToDictionary(pair => pair.label, pair => pair.at);

        return nodes.ToDictionary(node => node, node => ordinal[home[node]]);
    }
}
