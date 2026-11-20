namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// A uniform grid over one flattened route's segments, so a query for the segments near a box touches only
/// the cells the box covers instead of the whole route. The answer is exact: every segment whose box meets
/// the query box is returned, in ascending segment order.
/// </summary>
internal sealed class GraphStrokeIndex
{
    private const double CellSize = 48;

    private readonly IReadOnlyList<GraphPoint> _points;
    private readonly Dictionary<(int, int), List<int>> _cells = [];

    /// <summary>Indexes a polyline. A segment is named by the index of its end point.</summary>
    /// <param name="points">The flattened centerline.</param>
    public GraphStrokeIndex(IReadOnlyList<GraphPoint> points)
    {
        _points = points;
        for (var at = 1; at < points.Count; at++)
        {
            var (x0, y0, x1, y1) = Cells(Math.Min(points[at - 1].X, points[at].X), Math.Min(points[at - 1].Y, points[at].Y), Math.Max(points[at - 1].X, points[at].X), Math.Max(points[at - 1].Y, points[at].Y));
            for (var cx = x0; cx <= x1; cx++)
            {
                for (var cy = y0; cy <= y1; cy++)
                {
                    if (!_cells.TryGetValue((cx, cy), out var list))
                    {
                        _cells[(cx, cy)] = list = [];
                    }

                    list.Add(at);
                }
            }
        }
    }

    /// <summary>The segments whose box meets the given box.</summary>
    /// <param name="left">The box's smallest x.</param>
    /// <param name="top">The box's smallest y.</param>
    /// <param name="right">The box's largest x.</param>
    /// <param name="bottom">The box's largest y.</param>
    /// <returns>The segment indexes, ascending and distinct.</returns>
    public List<int> Near(double left, double top, double right, double bottom)
    {
        var (x0, y0, x1, y1) = Cells(left, top, right, bottom);
        var found = new SortedSet<int>();
        for (var cx = x0; cx <= x1; cx++)
        {
            for (var cy = y0; cy <= y1; cy++)
            {
                if (!_cells.TryGetValue((cx, cy), out var list))
                {
                    continue;
                }

                foreach (var at in list)
                {
                    var (p, q) = (_points[at - 1], _points[at]);
                    if (Math.Max(p.X, q.X) >= left && Math.Min(p.X, q.X) <= right && Math.Max(p.Y, q.Y) >= top && Math.Min(p.Y, q.Y) <= bottom)
                    {
                        found.Add(at);
                    }
                }
            }
        }

        return found.ToList();
    }

    private static (int X0, int Y0, int X1, int Y1) Cells(double left, double top, double right, double bottom) =>
        ((int)Math.Floor(left / CellSize), (int)Math.Floor(top / CellSize), (int)Math.Floor(right / CellSize), (int)Math.Floor(bottom / CellSize));
}
