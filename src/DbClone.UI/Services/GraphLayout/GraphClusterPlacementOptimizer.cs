namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Improves an already separated community placement by translating whole groups in two dimensions and
/// moving individual cards inside their group's region. Crossings, obscured relations and arrow lengths
/// may not increase. Ranked placements retain their order; stars retain their hub and surrounding spokes.
/// </summary>
internal sealed class GraphClusterPlacementOptimizer
{
    private const double Tolerance = 1e-6;
    private readonly GraphLayoutProfile _profile;
    private readonly GraphClusterSet _clusters;
    private readonly NodePlacement[] _cards;
    private readonly (int Child, int Parent)[] _edges;
    private readonly bool[] _ordered;
    private readonly int? _hub;
    private readonly NodePlacement[] _origin;
    private readonly CancellationToken _cancellation;
    private readonly Func<TimeSpan>? _timeLeft;
    private int _trials;

    private bool HasTime => _trials < GraphSearchBudget.MaximumClusterPlacementTrials
        && (_timeLeft is null || _timeLeft() > TimeSpan.Zero);

    private GraphClusterPlacementOptimizer(
        GraphLayoutProfile profile, GraphClusterSet clusters, IReadOnlyList<GraphPoint> positions,
        CancellationToken cancellation, Func<TimeSpan>? timeLeft, int? hub)
    {
        _profile = profile;
        _clusters = clusters;
        _cards = positions.Select((point, node) => new NodePlacement(node, point, profile.Widths[node], GraphCard.Height)).ToArray();
        _edges = profile.Edges.Where(edge => edge.Child != edge.Parent).ToArray();
        _ordered = _edges.Select(edge => !IsCycle(edge) && _cards[edge.Child].Centre.Y >= _cards[edge.Parent].Centre.Y - Tolerance).ToArray();
        _cancellation = cancellation;
        _timeLeft = timeLeft;
        _hub = hub;
        _origin = _cards.ToArray();
    }

    /// <summary>Returns improved card top-lefts without changing the caller's positions.</summary>
    /// <param name="profile">The component's relations and actual card widths.</param>
    /// <param name="clusters">The fixed community partition.</param>
    /// <param name="positions">The separated starting placement, in node order.</param>
    /// <param name="cancellation">Checked between every trial.</param>
    /// <param name="timeLeft">The enclosing request's remaining watchdog time.</param>
    /// <param name="hub">A pinned central hub; star placements do not constrain leaf directions by rank.</param>
    /// <returns>The best bounded placement found.</returns>
    public static IReadOnlyList<GraphPoint> Improve(
        GraphLayoutProfile profile, GraphClusterSet clusters, IReadOnlyList<GraphPoint> positions,
        CancellationToken cancellation = default, Func<TimeSpan>? timeLeft = null, int? hub = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (profile.Edges.Count > GraphSearchBudget.MaximumArrangerPricedStrokes
            || clusters.Members.Count > GraphSearchBudget.MaximumOptimizedClusterGroups)
        {
            return positions.ToArray();
        }

        var search = new GraphClusterPlacementOptimizer(profile, clusters, positions, cancellation, timeLeft, hub);
        search.Descend();
        return search._cards.Select(card => card.TopLeft).ToArray();
    }

    private void Descend()
    {
        var cost = Measure();
        for (var round = 0; round < GraphSearchBudget.MaximumClusterPlacementRounds && HasTime; round++)
        {
            var changed = false;
            for (var cluster = 0; cluster < _clusters.Members.Count && HasTime; cluster++)
            {
                if (_hub is null || !_clusters.Members[cluster].Contains(_hub.Value))
                    changed |= Move(_clusters.Members[cluster], GroupMoves(cluster).ToArray(), true, ref cost);
                changed |= SwapCards(cluster, ref cost);
            }

            for (var node = 0; node < _cards.Length && HasTime; node++)
            {
                if (node != _hub)
                    changed |= Move([node], CardMoves(node).ToArray(), false, ref cost);
            }

            if (!changed)
            {
                break;
            }
        }
    }

    private bool Move(IReadOnlyList<int> members, IReadOnlyList<GraphPoint> moves, bool wholeGroup,
        ref (int Blocked, int Crossings, double Length, double Longest) cost)
    {
        var original = members.Select(node => _cards[node]).ToArray();
        var best = new GraphPoint(0, 0);
        var region = Bounds(_clusters.Members[_clusters.ClusterOf[members[0]]]);
        foreach (var delta in moves.Distinct())
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!HasTime)
            {
                break;
            }

            _trials++;
            for (var at = 0; at < members.Count; at++)
            {
                _cards[members[at]] = original[at] with
                {
                    TopLeft = new GraphPoint(original[at].TopLeft.X + delta.X, original[at].TopLeft.Y + delta.Y)
                };
            }

            if (!Fits(members, region, wholeGroup) || !KeepsOrder())
            {
                continue;
            }

            var trial = Measure();
            if (Improves(trial, cost))
            {
                cost = trial;
                best = delta;
            }
        }

        for (var at = 0; at < members.Count; at++)
        {
            _cards[members[at]] = original[at] with
            {
                TopLeft = new GraphPoint(original[at].TopLeft.X + best.X, original[at].TopLeft.Y + best.Y)
            };
        }

        return best.X != 0 || best.Y != 0;
    }

    private static bool Improves(
        (int Blocked, int Crossings, double Length, double Longest) trial,
        (int Blocked, int Crossings, double Length, double Longest) cost) =>
        trial.Blocked <= cost.Blocked && trial.Crossings <= cost.Crossings
        && trial.Length <= cost.Length + Tolerance && trial.Longest <= cost.Longest + Tolerance
        && trial.CompareTo(cost) < 0;

    private bool SwapCards(int cluster, ref (int Blocked, int Crossings, double Length, double Longest) cost)
    {
        var changed = false;
        var members = _clusters.Members[cluster];
        for (var at = 0; at < members.Count && HasTime; at++)
        {
            for (var next = at + 1; next < members.Count && HasTime; next++)
            {
                _cancellation.ThrowIfCancellationRequested();
                _trials++;
                var first = _cards[members[at]];
                var second = _cards[members[next]];
                if (first.NodeIndex == _hub || second.NodeIndex == _hub) continue;
                var region = Bounds(members);
                _cards[first.NodeIndex] = first with { TopLeft = new GraphPoint(second.Centre.X - first.Width / 2, second.TopLeft.Y) };
                _cards[second.NodeIndex] = second with { TopLeft = new GraphPoint(first.Centre.X - second.Width / 2, first.TopLeft.Y) };
                if (Fits([first.NodeIndex], region, false) && Fits([second.NodeIndex], region, false) && KeepsOrder())
                {
                    var trial = Measure();
                    if (Improves(trial, cost))
                    {
                        cost = trial;
                        changed = true;
                        continue;
                    }
                }

                _cards[first.NodeIndex] = first;
                _cards[second.NodeIndex] = second;
            }
        }

        return changed;
    }

    private IEnumerable<GraphPoint> GroupMoves(int cluster)
    {
        var members = _clusters.Members[cluster];
        var box = Bounds(members);
        foreach (var delta in Steps(GraphPlacementConstants.ClusterGap))
        {
            yield return delta;
        }

        for (var other = 0; other < _clusters.Members.Count; other++)
        {
            if (other == cluster)
            {
                continue;
            }

            var links = _edges.Where(edge =>
                (_clusters.ClusterOf[edge.Child] == cluster && _clusters.ClusterOf[edge.Parent] == other)
                || (_clusters.ClusterOf[edge.Parent] == cluster && _clusters.ClusterOf[edge.Child] == other)).ToArray();
            if (links.Length == 0)
            {
                continue;
            }

            var near = Bounds(_clusters.Members[other]);
            var dx = links.Average(edge => OtherCentre(edge, cluster).X - OwnCentre(edge, cluster).X);
            var dy = links.Average(edge => OtherCentre(edge, cluster).Y - OwnCentre(edge, cluster).Y);
            yield return new GraphPoint(dx / 2, dy / 2);
            yield return new GraphPoint(dx, 0);
            yield return new GraphPoint(0, dy);
            foreach (var x in new[] { 0d, dx, near.Left - box.Left, near.Right - box.Right,
                (near.Left + near.Right - box.Left - box.Right) / 2 })
            {
                yield return new GraphPoint(x, near.Top - box.Bottom - GraphPlacementConstants.ClusterGap);
                yield return new GraphPoint(x, near.Bottom - box.Top + GraphPlacementConstants.ClusterGap);
            }

            foreach (var y in new[] { 0d, dy, near.Top - box.Top, near.Bottom - box.Bottom,
                (near.Top + near.Bottom - box.Top - box.Bottom) / 2 })
            {
                yield return new GraphPoint(near.Left - box.Right - GraphPlacementConstants.ClusterGap, y);
                yield return new GraphPoint(near.Right - box.Left + GraphPlacementConstants.ClusterGap, y);
            }
        }
    }

    private IEnumerable<GraphPoint> CardMoves(int node)
    {
        foreach (var delta in Steps(GraphPlacementConstants.CardGap / 2))
        {
            yield return delta;
        }

        var own = _cards[node];
        var neighbours = _profile.Neighbours[node];
        if (neighbours.Count == 0)
        {
            yield break;
        }

        var dx = neighbours.Average(peer => _cards[peer].Centre.X) - own.Centre.X;
        var dy = neighbours.Average(peer => _cards[peer].Centre.Y) - own.Centre.Y;
        yield return new GraphPoint(dx, 0);
        yield return new GraphPoint(0, dy);
        yield return new GraphPoint(dx / 2, dy / 2);
        foreach (var peer in neighbours)
        {
            var other = _cards[peer];
            yield return new GraphPoint(other.Centre.X - own.Centre.X, 0);
            yield return new GraphPoint(0, other.TopLeft.Y - own.Bottom - GraphPlacementConstants.CardGap);
            yield return new GraphPoint(0, other.Bottom - own.TopLeft.Y + GraphPlacementConstants.CardGap);
            foreach (var x in new[] { other.TopLeft.X - own.Right - GraphPlacementConstants.CardGap,
                other.Right - own.TopLeft.X + GraphPlacementConstants.CardGap })
            {
                yield return new GraphPoint(x, 0);
                yield return new GraphPoint(x, other.TopLeft.Y - own.TopLeft.Y);
                yield return new GraphPoint(x, other.Bottom - own.TopLeft.Y + GraphPlacementConstants.CardGap);
            }
        }
    }

    private static IEnumerable<GraphPoint> Steps(double step)
    {
        foreach (var scale in new[] { 1, 2, 4 })
        {
            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    if (x != 0 || y != 0)
                    {
                        yield return new GraphPoint(x * step * scale, y * step * scale);
                    }
                }
            }
        }
    }

    private bool Fits(IReadOnlyList<int> moved, (double Left, double Top, double Right, double Bottom) region, bool wholeGroup)
    {
        if (!wholeGroup)
        {
            var card = _cards[moved[0]];
            // Allow enough room to step beside a blocking table, while retaining the separate group below.
            var slack = Math.Max(card.Width, card.Height) + GraphPlacementConstants.CardGap;
            if (card.TopLeft.X < region.Left - slack
                || card.TopLeft.Y < region.Top - slack
                || card.Right > region.Right + slack
                || card.Bottom > region.Bottom + slack)
            {
                return false;
            }

            foreach (var peer in _clusters.Members[_clusters.ClusterOf[moved[0]]])
            {
                if (peer != moved[0] && Overlaps(Bounds([moved[0]]), Bounds([peer]), GraphPlacementConstants.CardGap))
                {
                    return false;
                }
            }
        }

        var cluster = _clusters.ClusterOf[moved[0]];
        var box = Bounds(_clusters.Members[cluster]);
        for (var other = 0; other < _clusters.Members.Count; other++)
        {
            if (other != cluster && Overlaps(box, Bounds(_clusters.Members[other]), GraphPlacementConstants.ClusterGap))
            {
                return false;
            }
        }

        return true;
    }

    private bool KeepsOrder()
    {
        if (_hub is { } hub)
            return GraphClusterStarLayout.Preserves(_origin, _cards, _clusters, hub);
        for (var at = 0; at < _edges.Length; at++)
        {
            var edge = _edges[at];
            if (_ordered[at] && _cards[edge.Child].Centre.Y < _cards[edge.Parent].Centre.Y - Tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsCycle((int Child, int Parent) edge) =>
        _profile.Group[edge.Child] == _profile.Group[edge.Parent] && _profile.GroupSize[edge.Child] > 1;

    private (int Blocked, int Crossings, double Length, double Longest) Measure()
    {
        var ports = GraphPortAssigner.Assign(_cards, _profile.Edges);
        if (!ports.IsComplete)
        {
            return (int.MaxValue, int.MaxValue, double.MaxValue, double.MaxValue);
        }

        var strokes = Enumerable.Range(0, _profile.Edges.Count)
            .Where(edge => _profile.Edges[edge].Child != _profile.Edges[edge].Parent)
            .Select(edge => (Edge: _profile.Edges[edge], From: ports.Ends[edge]!.Child.Point, To: ports.Ends[edge]!.Parent.Point)).ToArray();
        var crossings = 0;
        var blocked = 0;
        var length = 0d;
        var longest = 0d;
        for (var at = 0; at < strokes.Length; at++)
        {
            _cancellation.ThrowIfCancellationRequested();
            var stroke = strokes[at];
            var distance = GraphGeometry.Distance(stroke.From, stroke.To);
            length += distance;
            longest = Math.Max(longest, distance);
            if (_cards.Any(card => card.NodeIndex != stroke.Edge.Child && card.NodeIndex != stroke.Edge.Parent
                && GraphGeometry.SegmentHitsCardEnvelope(stroke.From, stroke.To, card)))
            {
                blocked++;
            }

            for (var next = at + 1; next < strokes.Length; next++)
            {
                if (GraphGeometry.SegmentsCross(stroke.From, stroke.To, strokes[next].From, strokes[next].To))
                {
                    crossings++;
                }
            }
        }

        return (blocked, crossings, length, longest);
    }

    private GraphPoint OwnCentre((int Child, int Parent) edge, int cluster) =>
        _cards[_clusters.ClusterOf[edge.Child] == cluster ? edge.Child : edge.Parent].Centre;

    private GraphPoint OtherCentre((int Child, int Parent) edge, int cluster) =>
        _cards[_clusters.ClusterOf[edge.Child] == cluster ? edge.Parent : edge.Child].Centre;

    private (double Left, double Top, double Right, double Bottom) Bounds(IReadOnlyList<int> members) =>
        (members.Min(node => _cards[node].TopLeft.X), members.Min(node => _cards[node].TopLeft.Y),
            members.Max(node => _cards[node].Right), members.Max(node => _cards[node].Bottom));

    private static bool Overlaps(
        (double Left, double Top, double Right, double Bottom) a,
        (double Left, double Top, double Right, double Bottom) b, double gap) =>
        a.Left < b.Right + gap - Tolerance && b.Left < a.Right + gap - Tolerance
        && a.Top < b.Bottom + gap - Tolerance && b.Top < a.Bottom + gap - Tolerance;
}
