namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The ground a radial arrangement reserves for the strokes that cross it. A relation is one straight stroke
/// painted behind the cards, so a card standing between the two it joins covers it. After the rings are laid
/// out and settled, this pass turns cards along their own ring, and where that is not enough widens a ring, for
/// as long as the number of relations a third card covers goes down. It never widens a card, moves a port,
/// bends a relation or drops one, and it decides nothing the arrangement has not already decided: the rings
/// keep their order and their members, only where a card stands on its ring and how far a ring is from the
/// core change.
/// </summary>
/// <remarks>
/// A relation is tested as the straight segment between its two card centres against the other cards'
/// <see cref="GraphGeometry.SegmentHitsCardEnvelope"/> envelope, the same clearance the written page is read
/// with. Every tie goes to the lower node index, clockwise before counter-clockwise.
/// </remarks>
internal sealed class GraphLaneReservation
{
    private const double OverlapTolerance = 0.5;

    private readonly IReadOnlyList<int> _nodes;
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly IReadOnlyList<double> _widths;
    private readonly int[] _ring;
    private readonly (int Child, int Parent)[] _relations;
    private readonly NodePlacement[] _cards;
    private readonly int[] _blocks;
    private readonly SortedDictionary<int, (double Radius, double Step)> _rings;
    private readonly GraphPoint _core;

    private GraphLaneReservation(
        IReadOnlyList<int> nodes,
        double[] x,
        double[] y,
        IReadOnlyList<double> widths,
        int[] ring,
        IReadOnlyDictionary<int, (double Radius, double Step)> rings,
        (int Child, int Parent)[] relations)
    {
        (_nodes, _x, _y, _widths, _ring, _relations) = (nodes, x, y, widths, ring, relations);
        _rings = new SortedDictionary<int, (double Radius, double Step)>(rings.ToDictionary(pair => pair.Key, pair => pair.Value));
        var inner = Enumerable.Range(0, nodes.Count).Where(at => ring[at] == 0).ToArray();
        _core = new GraphPoint(inner.Average(at => x[at]), inner.Average(at => y[at]));
        _cards = Enumerable.Range(0, nodes.Count).Select(Card).ToArray();
        _blocks = new int[relations.Length];
        for (var relation = 0; relation < relations.Length; relation++)
        {
            _blocks[relation] = Count(relation);
        }
    }

    /// <summary>Reserves the lanes of one settled component, updating its centres in place.</summary>
    /// <param name="nodes">The component's node indexes; every other array is indexed by position in it.</param>
    /// <param name="x">The card centres' x, which the pass updates.</param>
    /// <param name="y">The card centres' y, which the pass updates.</param>
    /// <param name="widths">The width of every card, by node index.</param>
    /// <param name="ring">The ring number of each card, by position.</param>
    /// <param name="rings">The radius and the step the component recorded for each ring it laid out.</param>
    /// <param name="relations">The component's relations as positions, self-references left out.</param>
    public static void Reserve(
        IReadOnlyList<int> nodes,
        double[] x,
        double[] y,
        IReadOnlyList<double> widths,
        int[] ring,
        IReadOnlyDictionary<int, (double Radius, double Step)> rings,
        (int Child, int Parent)[] relations)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (relations.Length == 0 || nodes.Count < 3)
        {
            return;
        }

        var reservation = new GraphLaneReservation(nodes, x, y, widths, ring, rings, relations);
        if (reservation.Hidden() > 0)
        {
            reservation.Turn();
            reservation.Grow();
        }
    }

    private int Hidden() => _blocks.Count(count => count > 0);

    private NodePlacement Card(int at) =>
        new(_nodes[at], new GraphPoint(_x[at] - (_widths[_nodes[at]] / 2), _y[at] - (GraphCard.Height / 2)), _widths[_nodes[at]], GraphCard.Height);

    /// <summary>How many cards other than its own two stand over one relation.</summary>
    private int Count(int relation)
    {
        var (child, parent) = _relations[relation];
        var (from, to) = (_cards[child].Centre, _cards[parent].Centre);
        var count = 0;
        for (var at = 0; at < _cards.Length; at++)
        {
            if (at != child && at != parent && GraphGeometry.SegmentHitsCardEnvelope(from, to, _cards[at]))
            {
                count++;
            }
        }

        return count;
    }

    private void Turn()
    {
        for (var round = 0; round < GraphSearchBudget.MaximumReservationRounds; round++)
        {
            var moved = false;
            foreach (var at in Starved().OrderBy(at => _nodes[at]))
            {
                moved |= Rotate(at, 1) || Rotate(at, -1);
            }

            if (!moved)
            {
                return;
            }
        }
    }

    /// <summary>The cards that are an end of a covered relation or that cover one.</summary>
    private HashSet<int> Starved()
    {
        var starved = new HashSet<int>();
        for (var relation = 0; relation < _relations.Length; relation++)
        {
            if (_blocks[relation] == 0)
            {
                continue;
            }

            var (child, parent) = _relations[relation];
            starved.Add(child);
            starved.Add(parent);
            for (var at = 0; at < _cards.Length; at++)
            {
                if (at != child && at != parent
                    && GraphGeometry.SegmentHitsCardEnvelope(_cards[child].Centre, _cards[parent].Centre, _cards[at]))
                {
                    starved.Add(at);
                }
            }
        }

        return starved;
    }

    /// <summary>Turns one card along its own ring, one step at a time, until the covered count falls.</summary>
    private bool Rotate(int at, int direction)
    {
        var (dx, dy) = (_x[at] - _core.X, _y[at] - _core.Y);
        var radius = Math.Sqrt((dx * dx) + (dy * dy));
        if (radius < 1e-6)
        {
            return false;
        }

        var angle = Math.Atan2(dy, dx);
        for (var step = 1; step <= GraphSearchBudget.MaximumReservationSteps; step++)
        {
            var turned = angle + (direction * step * GraphSearchBudget.ReservationAngleStep);
            var (nx, ny) = (_core.X + (radius * Math.Cos(turned)), _core.Y + (radius * Math.Sin(turned)));
            if (Overlaps(at, nx, ny))
            {
                return false;
            }

            if (TryMove(at, nx, ny))
            {
                return true;
            }
        }

        return false;
    }

    private bool Overlaps(int at, double nx, double ny)
    {
        for (var other = 0; other < _cards.Length; other++)
        {
            if (other == at)
            {
                continue;
            }

            var penetrationX = ((_widths[_nodes[at]] + _widths[_nodes[other]]) / 2) + GraphPlacementConstants.CardGap - Math.Abs(nx - _x[other]);
            var penetrationY = GraphCard.Height + GraphPlacementConstants.CardGap - Math.Abs(ny - _y[other]);
            if (penetrationX > OverlapTolerance && penetrationY > OverlapTolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Moves a card when that strictly lowers the number of covered relations; otherwise puts it back.</summary>
    private bool TryMove(int at, double nx, double ny)
    {
        var old = _cards[at];
        var next = (int[])_blocks.Clone();
        _cards[at] = new NodePlacement(old.NodeIndex, new GraphPoint(nx - (old.Width / 2), ny - (old.Height / 2)), old.Width, old.Height);
        for (var relation = 0; relation < _relations.Length; relation++)
        {
            var (child, parent) = _relations[relation];
            if (child == at || parent == at)
            {
                next[relation] = Count(relation);
                continue;
            }

            var (from, to) = (_cards[child].Centre, _cards[parent].Centre);
            next[relation] += (GraphGeometry.SegmentHitsCardEnvelope(from, to, _cards[at]) ? 1 : 0)
                - (GraphGeometry.SegmentHitsCardEnvelope(from, to, old) ? 1 : 0);
        }

        if (next.Count(count => count > 0) >= Hidden())
        {
            _cards[at] = old;
            return false;
        }

        (_x[at], _y[at]) = (nx, ny);
        Array.Copy(next, _blocks, next.Length);
        return true;
    }

    /// <summary>Widens a ring, and the rings outside it, while that lowers the covered count.</summary>
    private void Grow()
    {
        var growths = 0;
        var grew = true;
        while (grew && growths < GraphSearchBudget.MaximumReservationGrowths && Hidden() > 0)
        {
            grew = false;
            foreach (var ring in _rings.Keys.Where(key => key > 0).ToArray())
            {
                if (TryGrow(ring))
                {
                    growths++;
                    grew = true;
                    break;
                }
            }
        }
    }

    private bool TryGrow(int ring)
    {
        var radii = GrownRadii(ring);
        var before = (x: (double[])_x.Clone(), y: (double[])_y.Clone(), cards: (NodePlacement[])_cards.Clone(), blocks: (int[])_blocks.Clone());
        for (var at = 0; at < _cards.Length; at++)
        {
            if (radii.TryGetValue(_ring[at], out var delta) && delta > 0)
            {
                var (dx, dy) = (_x[at] - _core.X, _y[at] - _core.Y);
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                (_x[at], _y[at]) = (_core.X + (dx * (distance + delta) / distance), _core.Y + (dy * (distance + delta) / distance));
                _cards[at] = Card(at);
            }
        }

        var improved = !AnyOverlap() && Recount() < before.blocks.Count(count => count > 0);
        if (improved)
        {
            foreach (var (key, delta) in radii.Where(pair => pair.Value > 0))
            {
                _rings[key] = (_rings[key].Radius + delta, _rings[key].Step);
            }

            return true;
        }

        Array.Copy(before.x, _x, _x.Length);
        Array.Copy(before.y, _y, _y.Length);
        Array.Copy(before.cards, _cards, _cards.Length);
        Array.Copy(before.blocks, _blocks, _blocks.Length);
        return false;
    }

    /// <summary>
    /// How far each ring moves out when <paramref name="ring"/> grows by the step recorded for it: the ring
    /// itself, and every ring outside it that the recorded chain would otherwise leave inside the grown one,
    /// so no ring is ever left inside the one it grew past.
    /// </summary>
    private Dictionary<int, double> GrownRadii(int ring)
    {
        var keys = _rings.Keys.ToArray();
        var at = Array.IndexOf(keys, ring);
        var delta = new Dictionary<int, double> { [ring] = _rings[ring].Step };
        var previous = _rings[ring].Radius + _rings[ring].Step;
        for (var next = at + 1; next < keys.Length; next++)
        {
            var wanted = Math.Max(_rings[keys[next]].Radius, previous + _rings[keys[next]].Step);
            delta[keys[next]] = wanted - _rings[keys[next]].Radius;
            previous = wanted;
        }

        return delta;
    }

    private bool AnyOverlap()
    {
        for (var at = 0; at < _cards.Length; at++)
        {
            if (Overlaps(at, _x[at], _y[at]))
            {
                return true;
            }
        }

        return false;
    }

    private int Recount()
    {
        for (var relation = 0; relation < _relations.Length; relation++)
        {
            _blocks[relation] = Count(relation);
        }

        return Hidden();
    }
}
