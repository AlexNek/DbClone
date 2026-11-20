namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// A uniform grid over the reserved routes, so a candidate asks only the routes whose strokes lie in the
/// cells it covers instead of every route drawn. A route is registered by the cells its segments cover; a
/// route too long to cell cheaply is kept on a short list every query also reads. The answer is the same as
/// asking every reserved route: the routes whose box meets the query's, grown by the margin, in a stable
/// order. The index is a stack — the last route pushed is the first popped — and it can also put a route in
/// place of the one with its relation index, which is how a substituted route is recorded.
/// </summary>
internal sealed class GraphRouteIndex
{
    /// <summary>The side of one grid cell, in pixels.</summary>
    private const double CellSize = 48;

    /// <summary>
    /// How many cells one route may claim before it is kept on the wide list instead. A diagonal stroke
    /// across a large picture owns a box of cells far larger than its length, and registering it would cost
    /// more than testing every query against it.
    /// </summary>
    private const int MaximumCellsPerRoute = 4096;

    private readonly Dictionary<(int X, int Y), List<int>> _cells = [];
    private readonly List<GraphFlatRoute> _routes = [];
    private readonly List<(int X, int Y)[]> _covered = [];
    private readonly Dictionary<int, int> _byEdge = [];
    private readonly HashSet<int> _wide = [];

    /// <summary>Gets how many routes are reserved.</summary>
    public int Count => _routes.Count;

    /// <summary>Reserves a route.</summary>
    /// <param name="route">The flattened route.</param>
    public void Push(GraphFlatRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        var id = _routes.Count;
        _routes.Add(route);
        _covered.Add([]);
        _byEdge[route.EdgeIndex] = id;
        Attach(id);
    }

    /// <summary>Releases the route reserved last.</summary>
    public void Pop()
    {
        var id = _routes.Count - 1;
        Detach(id);
        _byEdge.Remove(_routes[id].EdgeIndex);
        _routes.RemoveAt(id);
        _covered.RemoveAt(id);
        _wide.Remove(id);
    }

    /// <summary>Puts a route in place of the reserved route of the same relation, or reserves it when none is.</summary>
    /// <param name="route">The flattened route.</param>
    public void Replace(GraphFlatRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        if (!_byEdge.TryGetValue(route.EdgeIndex, out var id))
        {
            Push(route);
            return;
        }

        Detach(id);
        _routes[id] = route;
        Attach(id);
    }

    /// <summary>The reserved routes whose box meets the given route's, grown by the margin.</summary>
    /// <param name="route">The candidate route.</param>
    /// <param name="margin">How far to grow the query.</param>
    /// <param name="excluded">Relation indexes to leave out of the answer, if any.</param>
    /// <returns>The reserved routes, ordered by relation index.</returns>
    public IReadOnlyList<GraphFlatRoute> Near(GraphFlatRoute route, double margin, IReadOnlyCollection<int>? excluded = null)
    {
        ArgumentNullException.ThrowIfNull(route);

        var found = new SortedSet<int>();
        for (var at = 1; at < route.Points.Count; at++)
        {
            var (start, end) = (route.Points[at - 1], route.Points[at]);
            foreach (var cell in Cells(
                Math.Min(start.X, end.X) - margin,
                Math.Min(start.Y, end.Y) - margin,
                Math.Max(start.X, end.X) + margin,
                Math.Max(start.Y, end.Y) + margin))
            {
                if (!_cells.TryGetValue(cell, out var list))
                {
                    continue;
                }

                foreach (var id in list)
                {
                    found.Add(id);
                }
            }
        }

        found.UnionWith(_wide);

        return found
            .Where(id => excluded is null || !excluded.Contains(_routes[id].EdgeIndex))
            .Where(id => _routes[id].Near(route, margin))
            .Select(id => _routes[id])
            .OrderBy(reserved => reserved.EdgeIndex)
            .ToArray();
    }

    /// <summary>Registers which cells a route owns, or lists it as wide when its box is too large to cell.</summary>
    private void Attach(int id)
    {
        var route = _routes[id];
        var covered = new HashSet<(int X, int Y)>();
        var wide = false;

        for (var at = 1; at < route.Points.Count && !wide; at++)
        {
            var (start, end) = (route.Points[at - 1], route.Points[at]);
            foreach (var cell in Cells(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)))
            {
                covered.Add(cell);
                if (covered.Count > MaximumCellsPerRoute)
                {
                    wide = true;
                    break;
                }
            }
        }

        _covered[id] = wide ? [] : covered.ToArray();
        if (wide)
        {
            _wide.Add(id);
            return;
        }

        _wide.Remove(id);
        foreach (var cell in _covered[id])
        {
            if (!_cells.TryGetValue(cell, out var list))
            {
                _cells[cell] = list = [];
            }

            list.Add(id);
        }
    }

    /// <summary>Withdraws a route from the grid, leaving the empty cells to be reused.</summary>
    private void Detach(int id)
    {
        if (_wide.Remove(id))
        {
            return;
        }

        foreach (var cell in _covered[id])
        {
            if (!_cells.TryGetValue(cell, out var list))
            {
                continue;
            }

            list.Remove(id);
        }
    }

    private static IEnumerable<(int X, int Y)> Cells(double left, double top, double right, double bottom)
    {
        var (x0, y0, x1, y1) = (
            (int)Math.Floor(left / CellSize), (int)Math.Floor(top / CellSize),
            (int)Math.Floor(right / CellSize), (int)Math.Floor(bottom / CellSize));

        for (var x = x0; x <= x1; x++)
        {
            for (var y = y0; y <= y1; y++)
            {
                yield return (x, y);
            }
        }
    }
}
