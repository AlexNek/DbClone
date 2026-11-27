namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Improves a separated placement through group, branch and card moves. Crossings and obscured relations
/// may not increase; stars may change sectors and use longer arrows to clear individual obstructions.
/// Ranked placements retain their local regions, order and length ceilings.
/// </summary>
internal sealed class GraphClusterPlacementOptimizer
{
    private const double Tolerance = 1e-6;
    private readonly GraphLayoutProfile _profile;
    private readonly GraphClusterSet _clusters;
    private readonly IReadOnlyList<(int Community, IReadOnlyList<int> Members)> _regions;
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
        _regions = GraphClusterRegions.Split(profile, clusters, hub);
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
                if (_hub is { } root && !_clusters.Members[cluster].Contains(root))
                {
                    changed |= Move(_clusters.Members[cluster], GraphStarPlacementMoves.Orbit(_cards, _clusters.Members[cluster], root).ToArray(), true, ref cost);
                    changed |= Reorient(_clusters.Members[cluster], root, null, ref cost);
                    changed |= Turn(_clusters.Members[cluster], ref cost);
                }
            }

            if (_hub is { } hub)
                foreach (var branch in GraphStarPlacementMoves.Branches(_profile, _clusters, hub))
                {
                    if (!HasTime) break;
                    changed |= Move(branch, GraphStarPlacementMoves.Orbit(_cards, branch, hub).ToArray(), false, ref cost);
                    if (branch.Count > 1) changed |= Turn(branch, ref cost);
                }

            foreach (var node in Enumerable.Range(0, _cards.Length))
            {
                if (!HasTime) break;
                if (node != _hub)
                    changed |= Move([node], CardMoves(node).ToArray(), false, ref cost);
            }

            if (_hub is { } centre)
                foreach (var branch in GraphStarPlacementMoves.Branches(_profile, _clusters, centre).Where(branch => branch.Count > 1))
                    changed |= Reorient(branch, centre, GraphStarPlacementMoves.Compact(_profile, branch, centre), ref cost);

            if (_hub is not null) changed |= SwapRegions(ref cost);
            if (!changed)
            {
                break;
            }
        }
    }

    private bool Move(IReadOnlyList<int> members, IReadOnlyList<GraphPoint> moves, bool wholeGroup,
        ref (int Blocked, int Crossings, int Obstructions, double Length, double Longest) cost)
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

    private bool Improves(
        (int Blocked, int Crossings, int Obstructions, double Length, double Longest) trial,
        (int Blocked, int Crossings, int Obstructions, double Length, double Longest) cost) =>
        _hub is not null
            ? trial.Blocked <= cost.Blocked && trial.Crossings <= cost.Crossings
                && (trial.Blocked < cost.Blocked || trial.Crossings < cost.Crossings || trial.Obstructions < cost.Obstructions
                    || (trial.Length <= cost.Length + Tolerance && trial.Longest <= cost.Longest + Tolerance))
                && trial.CompareTo(cost) < 0
            : trial.Blocked <= cost.Blocked && trial.Crossings <= cost.Crossings
                && trial.Length <= cost.Length + Tolerance && trial.Longest <= cost.Longest + Tolerance
                && (trial.Blocked, trial.Crossings, trial.Length, trial.Longest)
                    .CompareTo((cost.Blocked, cost.Crossings, cost.Length, cost.Longest)) < 0;

    private bool Turn(IReadOnlyList<int> members, ref (int Blocked, int Crossings, int Obstructions, double Length, double Longest) cost)
    {
        var original = members.Select(node => _cards[node]).ToArray();
        var centre = new GraphPoint(original.Average(card => card.Centre.X), original.Average(card => card.Centre.Y));
        var best = original;
        var region = Bounds(_clusters.Members[_clusters.ClusterOf[members[0]]]);
        for (var step = 1; step < 24 && HasTime; step++)
        {
            _cancellation.ThrowIfCancellationRequested();
            _trials++;
            var angle = step * Math.PI / 12;
            for (var at = 0; at < members.Count; at++)
            {
                var dx = original[at].Centre.X - centre.X;
                var dy = original[at].Centre.Y - centre.Y;
                _cards[members[at]] = original[at] with { TopLeft = new GraphPoint(
                    centre.X + dx * Math.Cos(angle) - dy * Math.Sin(angle) - original[at].Width / 2,
                    centre.Y + dx * Math.Sin(angle) + dy * Math.Cos(angle) - original[at].Height / 2) };
            }
            if (!Fits(members, region, false) || !KeepsOrder()) continue;
            var trial = Measure();
            if (!Improves(trial, cost)) continue;
            cost = trial;
            best = members.Select(node => _cards[node]).ToArray();
        }
        for (var at = 0; at < members.Count; at++) _cards[members[at]] = best[at];
        return !best.SequenceEqual(original);
    }

    private bool Reorient(IReadOnlyList<int> members, int hub, IReadOnlyList<GraphPoint>? shape,
        ref (int Blocked, int Crossings, int Obstructions, double Length, double Longest) cost)
    {
        var original = members.Select(node => _cards[node]).ToArray();
        var centre = new GraphPoint(original.Average(card => card.Centre.X), original.Average(card => card.Centre.Y));
        var points = shape ?? original.Select(card => card.Centre).ToArray();
        var localCentre = new GraphPoint(points.Average(point => point.X), points.Average(point => point.Y));
        var root = _cards[hub].Centre;
        var radius = GraphGeometry.Distance(centre, root);
        var facing = Math.Atan2(centre.Y - root.Y, centre.X - root.X);
        var best = original;
        var region = Bounds(_clusters.Members[_clusters.ClusterOf[members[0]]]);
        foreach (var scale in new[] { 1d, 1.5, 0.75 })
            for (var sector = 0; sector < 16 && HasTime; sector++)
                for (var turn = 0; turn < (shape is null ? 2 : 4) && HasTime; turn++)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    _trials++;
                    var angle = facing + sector * Math.PI / 8;
                    var rotation = shape is null ? angle - facing + turn * Math.PI : turn * Math.PI / 2;
                    for (var at = 0; at < members.Count; at++)
                    {
                        var dx = points[at].X - localCentre.X;
                        var dy = points[at].Y - localCentre.Y;
                        _cards[members[at]] = original[at] with { TopLeft = new GraphPoint(
                            root.X + scale * radius * Math.Cos(angle) + dx * Math.Cos(rotation) - dy * Math.Sin(rotation) - original[at].Width / 2,
                            root.Y + scale * radius * Math.Sin(angle) + dx * Math.Sin(rotation) + dy * Math.Cos(rotation) - original[at].Height / 2) };
                    }
                    if (!Fits(members, region, false) || !KeepsOrder()) continue;
                    var trial = Measure();
                    if (!Improves(trial, cost)) continue;
                    cost = trial;
                    best = members.Select(node => _cards[node]).ToArray();
                }
        for (var at = 0; at < members.Count; at++) _cards[members[at]] = best[at];
        return !best.SequenceEqual(original);
    }

    private bool SwapRegions(ref (int Blocked, int Crossings, int Obstructions, double Length, double Longest) cost)
    {
        var changed = false;
        var regions = _regions.Where(part => !part.Members.Contains(_hub!.Value)).ToArray();
        for (var first = 0; first < regions.Length && HasTime; first++)
            for (var second = first + 1; second < regions.Length && HasTime; second++)
            {
                var a = regions[first].Members;
                var b = regions[second].Members;
                var members = a.Concat(b).ToArray();
                var original = members.Select(node => _cards[node]).ToArray();
                var centreA = new GraphPoint(a.Average(node => _cards[node].Centre.X), a.Average(node => _cards[node].Centre.Y));
                var centreB = new GraphPoint(b.Average(node => _cards[node].Centre.X), b.Average(node => _cards[node].Centre.Y));
                var best = original;
                for (var turn = 0; turn < 4 && HasTime; turn++)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    _trials++;
                    for (var at = 0; at < members.Length; at++)
                    {
                        var inA = at < a.Count;
                        var from = inA ? centreA : centreB;
                        var to = inA ? centreB : centreA;
                        var sign = (turn & (inA ? 1 : 2)) == 0 ? 1 : -1;
                        _cards[members[at]] = original[at] with { TopLeft = new GraphPoint(
                            to.X + sign * (original[at].Centre.X - from.X) - original[at].Width / 2,
                            to.Y + sign * (original[at].Centre.Y - from.Y) - original[at].Height / 2) };
                    }
                    if (!Fits(a, Bounds(a), false) || !Fits(b, Bounds(b), false) || !KeepsOrder()) continue;
                    var trial = Measure();
                    if (!Improves(trial, cost)) continue;
                    cost = trial;
                    best = members.Select(node => _cards[node]).ToArray();
                }
                for (var at = 0; at < members.Length; at++) _cards[members[at]] = best[at];
                changed |= !best.SequenceEqual(original);
            }
        return changed;
    }

    private bool SwapCards(int cluster, ref (int Blocked, int Crossings, int Obstructions, double Length, double Longest) cost)
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
        if (_hub is not null)
            foreach (var node in members)
                foreach (var delta in GraphStarPlacementMoves.Endpoints(_profile, _cards, node)) yield return delta;
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
        if (_hub is not null)
        {
            var clearances = GraphStarPlacementMoves.Clearances(_profile, _cards, node).ToArray();
            foreach (var delta in clearances) yield return delta;
            if (clearances.Length > 0 && _profile.Neighbours[node].Count > 0)
            {
                var attachment = _profile.Neighbours[node].OrderByDescending(peer => _clusters.ClusterOf[peer] == _clusters.ClusterOf[node])
                    .ThenByDescending(peer => _profile.Neighbours[peer].Count).ThenBy(peer => peer).First();
                foreach (var delta in GraphStarPlacementMoves.Orbit(_cards, [node], attachment)) yield return delta;
            }
            foreach (var delta in GraphStarPlacementMoves.Endpoints(_profile, _cards, node)) yield return delta;
        }
        foreach (var delta in Steps(GraphPlacementConstants.CardGap / 2))
        {
            yield return delta;
        }

        var own = _cards[node];
        if (_hub is not null && _profile.Neighbours[node].Any(peer => _clusters.ClusterOf[peer] != _clusters.ClusterOf[node]))
        {
            var box = Bounds(_clusters.Members[_clusters.ClusterOf[node]]);
            foreach (var x in new[] { box.Left - own.Width / 2 - GraphPlacementConstants.CardGap, box.Right + own.Width / 2 + GraphPlacementConstants.CardGap })
                yield return new GraphPoint(x - own.Centre.X, 0);
            foreach (var y in new[] { box.Top - own.Height / 2 - GraphPlacementConstants.CardGap, box.Bottom + own.Height / 2 + GraphPlacementConstants.CardGap })
                yield return new GraphPoint(0, y - own.Centre.Y);
        }
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
            if (_hub is null && (card.TopLeft.X < region.Left - slack
                || card.TopLeft.Y < region.Top - slack
                || card.Right > region.Right + slack
                || card.Bottom > region.Bottom + slack))
            {
                return false;
            }

            foreach (var node in moved)
                foreach (var peer in _clusters.Members[_clusters.ClusterOf[node]])
                {
                    if (peer != node && Overlaps(Bounds([node]), Bounds([peer]), GraphPlacementConstants.CardGap))
                        return false;
                }
        }

        foreach (var regionPart in _regions.Where(part => part.Members.Any(moved.Contains)))
            foreach (var other in _regions)
            {
                if (other.Community != regionPart.Community
                    && Overlaps(Bounds(regionPart.Members), Bounds(other.Members), GraphPlacementConstants.ClusterGap))
                    return false;
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

    private (int Blocked, int Crossings, int Obstructions, double Length, double Longest) Measure()
    {
        var ports = GraphPortAssigner.Assign(_cards, _profile.Edges);
        if (!ports.IsComplete)
        {
            return (int.MaxValue, int.MaxValue, int.MaxValue, double.MaxValue, double.MaxValue);
        }

        var strokes = Enumerable.Range(0, _profile.Edges.Count)
            .Where(edge => _profile.Edges[edge].Child != _profile.Edges[edge].Parent)
            .Select(edge => (Edge: _profile.Edges[edge], From: ports.Ends[edge]!.Child.Point, To: ports.Ends[edge]!.Parent.Point)).ToArray();
        var crossings = 0;
        var blocked = 0;
        var obstructions = 0;
        var length = 0d;
        var longest = 0d;
        for (var at = 0; at < strokes.Length; at++)
        {
            _cancellation.ThrowIfCancellationRequested();
            var stroke = strokes[at];
            var distance = GraphGeometry.Distance(stroke.From, stroke.To);
            length += distance;
            longest = Math.Max(longest, distance);
            var blockers = _cards.Count(card => card.NodeIndex != stroke.Edge.Child && card.NodeIndex != stroke.Edge.Parent
                && GraphGeometry.SegmentHitsCardEnvelope(stroke.From, stroke.To, card));
            // Clearing one of several cards must make progress even before the entire stroke is clear.
            obstructions += blockers;
            if (blockers > 0) blocked++;

            for (var next = at + 1; next < strokes.Length; next++)
            {
                if (GraphGeometry.SegmentsCross(stroke.From, stroke.To, strokes[next].From, strokes[next].To))
                {
                    crossings++;
                }
            }
        }

        return (blocked, crossings, obstructions, length, longest);
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
