using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// One candidate arrangement of the cards on the lattice, together with the measurement that decides
/// between candidates: how many arrows cross each other, how many are drawn over a card they do not
/// join, and how far they travel — all counted in cells, so no score depends on a card's pixels.
/// </summary>
/// <remarks>
/// <para>
/// Placement keeps the candidate that measures best instead of obeying a rule it cannot check. The
/// ranked arrangement is the first candidate, so measuring cannot make a picture worse: a candidate
/// replaces it only when its own arrows cross less or run shorter, which is what lets where a table
/// goes depend on the graph in front of it rather than on a fixed preference (pass 11).
/// </para>
/// <para>
/// Comparing two arrows costs a few multiplications, and a candidate is always compared against the
/// one that stands, so the work is bounded by <see cref="ComparisonBudget"/> pairs on a catalog too
/// large to compare in full. That bound is deterministic — it always stops at the same pair — so the
/// same catalog still draws the same picture (T5).
/// </para>
/// </remarks>
internal sealed class GraphArrangement
{
    /// <summary>What one pair of crossed arrows costs, in cells of travel.</summary>
    private const double CrossingWeight = 4;

    /// <summary>
    /// What one arrow drawn over a card it does not join costs, in cells of travel. Heavy enough
    /// that an arrangement is never preferred for being shorter while it puts a card in the way: the
    /// router has to turn around such a card, and a column of cards can leave it no corridor at all.
    /// </summary>
    private const double BlockedWeight = 100;

    /// <summary>
    /// What one column of picture width costs. Small enough that a crossing still outweighs it many
    /// times over, large enough that two arrangements which measure the same stay the narrower one.
    /// </summary>
    private const double ColumnWeight = 0.25;

    /// <summary>How many arrow pairs are compared before the count is taken as a proxy.</summary>
    private const int ComparisonBudget = 250_000;

    /// <summary>
    /// How much of a row a card fills. A card is drawn at the top of its row and the row gap is left
    /// empty under it, so a span that slips through that gap passes beside the card, not over it —
    /// and counting the empty part of the row as card makes a picture drawn on one line measure worse
    /// than the same picture wrapped to open a corridor that was there all along.
    /// </summary>
    private static readonly double CardInRow =
        GraphCard.Height / (GraphCard.Height + GraphRankedPlacement.RowGap);

    private readonly CatalogGraphModel _model;
    private readonly IReadOnlyList<List<int>> _units;
    private readonly (int Width, int Height)[] _shapes;
    private readonly int[] _unitOfNode;

    /// <summary>Holds one arrangement of every unit.</summary>
    /// <param name="model">The analyzed graph whose arrows are measured.</param>
    /// <param name="units">The members of every unit.</param>
    /// <param name="shapes">The cell rectangle each unit occupies.</param>
    /// <param name="rowOfUnit">The top row of every unit.</param>
    /// <param name="columnOfUnit">The left column of every unit.</param>
    public GraphArrangement(
        CatalogGraphModel model,
        IReadOnlyList<List<int>> units,
        IReadOnlyList<(int Width, int Height)> shapes,
        IReadOnlyList<int> rowOfUnit,
        IReadOnlyList<int> columnOfUnit)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(rowOfUnit);
        ArgumentNullException.ThrowIfNull(columnOfUnit);

        _model = model;
        _units = units;
        _shapes = [.. shapes];
        RowOfUnit = [.. rowOfUnit];
        ColumnOfUnit = [.. columnOfUnit];

        _unitOfNode = new int[model.Nodes.Count];
        for (var unit = 0; unit < units.Count; unit++)
        {
            foreach (var member in units[unit])
            {
                _unitOfNode[member] = unit;
            }
        }
    }

    /// <summary>Gets the top row of every unit.</summary>
    public int[] RowOfUnit { get; }

    /// <summary>Gets the left column of every unit.</summary>
    public int[] ColumnOfUnit { get; }

    /// <summary>Gets how many units the arrangement holds.</summary>
    public int UnitCount => _units.Count;

    /// <summary>Gets the cell rectangle one unit occupies.</summary>
    /// <param name="unit">The unit to measure.</param>
    /// <returns>Its width and height, in cells.</returns>
    public (int Width, int Height) ShapeOf(int unit) => _shapes[unit];

    /// <summary>Gets the members of one unit, so an arrangement can be read back as nodes.</summary>
    /// <param name="unit">The unit to read.</param>
    /// <returns>The node indexes the unit holds.</returns>
    public IReadOnlyList<int> MembersOf(int unit) => _units[unit];

    /// <summary>Gets the unit one node belongs to.</summary>
    /// <param name="node">The node to look up.</param>
    /// <returns>The unit that holds it.</returns>
    public int UnitOf(int node) => _unitOfNode[node];

    /// <summary>Gets the horizontal centre of a unit, in cells.</summary>
    /// <param name="unit">The unit to measure.</param>
    /// <returns>The x of the unit's centre.</returns>
    public double CentreX(int unit) => ColumnOfUnit[unit] + (_shapes[unit].Width / 2.0);

    /// <summary>
    /// Gets the vertical centre of a unit where its cards are drawn, in cells. A card fills the top of
    /// its row and the row gap is left empty under it, so the centre of a row of cards sits above the
    /// centre of the row's own cell, and a span measured between cell centres is measured between
    /// points the cards do not reach.
    /// </summary>
    /// <param name="unit">The unit to measure.</param>
    /// <returns>The y of the unit's drawn centre.</returns>
    public double CentreY(int unit) =>
        RowOfUnit[unit] + (((_shapes[unit].Height - 1) + CardInRow) / 2.0);

    /// <summary>Gets how many columns the arrangement spans.</summary>
    /// <returns>The number of cell columns, from the leftmost card to the rightmost.</returns>
    public int Columns()
    {
        var left = int.MaxValue;
        var right = int.MinValue;

        for (var unit = 0; unit < UnitCount; unit++)
        {
            left = Math.Min(left, ColumnOfUnit[unit]);
            right = Math.Max(right, ColumnOfUnit[unit] + _shapes[unit].Width);
        }

        return right - left;
    }

    /// <summary>Gets how many rows the arrangement spans.</summary>
    /// <returns>The number of cell rows, from the top card to the bottom.</returns>
    public int Rows()
    {
        var top = int.MaxValue;
        var bottom = int.MinValue;

        for (var unit = 0; unit < UnitCount; unit++)
        {
            top = Math.Min(top, RowOfUnit[unit]);
            bottom = Math.Max(bottom, RowOfUnit[unit] + _shapes[unit].Height);
        }

        return bottom - top;
    }

    /// <summary>Copies the arrangement, so a candidate can be moved without touching its source.</summary>
    /// <returns>An independent arrangement with the same cells.</returns>
    public GraphArrangement Copy() => new(_model, _units, _shapes, RowOfUnit, ColumnOfUnit);

    /// <summary>
    /// Copies the arrangement with every unit moved to another column, which is how a candidate that
    /// only rearranges the columns is measured against the one it came from.
    /// </summary>
    /// <param name="columnOfUnit">The left column of every unit.</param>
    /// <returns>An independent arrangement with the same rows and the given columns.</returns>
    public GraphArrangement WithColumns(IReadOnlyList<int> columnOfUnit) =>
        new(_model, _units, _shapes, RowOfUnit, columnOfUnit);

    /// <summary>Whether a unit could stand at one cell without sharing it with another unit.</summary>
    /// <param name="unit">The unit to place.</param>
    /// <param name="row">The top row it would take.</param>
    /// <param name="column">The left column it would take.</param>
    /// <param name="except">A unit to leave out of the test, or -1.</param>
    /// <returns>Whether the cell is free for it.</returns>
    public bool Fits(int unit, int row, int column, int except)
    {
        var (width, height) = _shapes[unit];

        for (var other = 0; other < UnitCount; other++)
        {
            if (other == unit || other == except)
            {
                continue;
            }

            var (otherWidth, otherHeight) = _shapes[other];
            var apart = column + width <= ColumnOfUnit[other]
                || ColumnOfUnit[other] + otherWidth <= column
                || row + height <= RowOfUnit[other]
                || RowOfUnit[other] + otherHeight <= row;

            if (!apart)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Measures the arrangement: every arrow drawn as a straight span between the two cards it joins,
    /// crossing and blocking counted against the other arrows and cards of the same arrangement.
    /// </summary>
    /// <returns>How bad the arrangement is; smaller is better.</returns>
    public double Cost() => Measure().Cost;

    /// <summary>
    /// Measures the arrangement, and says separately how many arrows it draws over a card they do not
    /// join — the one fault a picture's width can cause by itself, and therefore the one count that
    /// decides how wide a picture is drawn.
    /// </summary>
    /// <returns>How bad the arrangement is, and how many arrows it blocks.</returns>
    public (double Cost, int Blocked) Measure()
    {
        var edges = _model.Edges;
        var fromX = new double[edges.Count];
        var fromY = new double[edges.Count];
        var toX = new double[edges.Count];
        var toY = new double[edges.Count];
        var childUnit = new int[edges.Count];
        var parentUnit = new int[edges.Count];
        var drawn = new bool[edges.Count];

        for (var edge = 0; edge < edges.Count; edge++)
        {
            var child = _unitOfNode[edges[edge].ChildIndex];
            var parent = _unitOfNode[edges[edge].ParentIndex];

            // A self reference is a loop drawn around one card, so it states no span and is measured
            // by the loop's own rules, never as an arrow across the picture.
            if (child == parent)
            {
                continue;
            }

            childUnit[edge] = child;
            parentUnit[edge] = parent;
            fromX[edge] = CentreX(child);
            fromY[edge] = CentreY(child);
            toX[edge] = CentreX(parent);
            toY[edge] = CentreY(parent);
            drawn[edge] = true;
        }

        var travel = 0.0;
        var crossings = 0;
        var blocked = 0;
        var comparisons = 0;

        for (var first = 0; first < edges.Count; first++)
        {
            if (!drawn[first])
            {
                continue;
            }

            travel += Math.Abs(toX[first] - fromX[first]) + Math.Abs(toY[first] - fromY[first]);

            for (var second = first + 1; second < edges.Count; second++)
            {
                if (!drawn[second])
                {
                    continue;
                }

                if (comparisons == ComparisonBudget)
                {
                    break;
                }

                comparisons++;
                if (Crosses(
                    fromX[first], fromY[first], toX[first], toY[first],
                    fromX[second], fromY[second], toX[second], toY[second]))
                {
                    crossings++;
                }
            }

            for (var unit = 0; unit < UnitCount; unit++)
            {
                if (unit == childUnit[first] || unit == parentUnit[first])
                {
                    continue;
                }

                if (Crosses(unit, fromX[first], fromY[first], toX[first], toY[first]))
                {
                    blocked++;
                }
            }
        }

        return (
            (CrossingWeight * crossings) + (BlockedWeight * blocked) + travel + (ColumnWeight * Columns()),
            blocked);
    }

    /// <summary>Whether a span passes through the card a unit is drawn as, not through the whole cell.</summary>
    /// <param name="unit">The card to test against.</param>
    /// <param name="fromX">The span's first x.</param>
    /// <param name="fromY">The span's first y.</param>
    /// <param name="toX">The span's last x.</param>
    /// <param name="toY">The span's last y.</param>
    /// <returns>Whether the span meets the card.</returns>
    private bool Crosses(int unit, double fromX, double fromY, double toX, double toY)
    {
        var (width, height) = _shapes[unit];
        var left = ColumnOfUnit[unit];
        var top = RowOfUnit[unit];
        var bottom = top + height - 1 + CardInRow;

        var dx = toX - fromX;
        var dy = toY - fromY;

        if (dx == 0 && dy == 0)
        {
            return false;
        }

        var entering = 0.0;
        var leaving = 1.0;

        return Clip(-dx, fromX - left, ref entering, ref leaving)
            && Clip(dx, left + width - fromX, ref entering, ref leaving)
            && Clip(-dy, fromY - top, ref entering, ref leaving)
            && Clip(dy, bottom - fromY, ref entering, ref leaving)
            && entering < leaving;
    }

    /// <summary>Whether two straight spans properly meet, which is one crossing to read past.</summary>
    /// <param name="ax">The first span's first x.</param>
    /// <param name="ay">The first span's first y.</param>
    /// <param name="bx">The first span's last x.</param>
    /// <param name="by">The first span's last y.</param>
    /// <param name="cx">The second span's first x.</param>
    /// <param name="cy">The second span's first y.</param>
    /// <param name="dx">The second span's last x.</param>
    /// <param name="dy">The second span's last y.</param>
    /// <returns>Whether the spans cross each other.</returns>
    private static bool Crosses(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        var first = Side(ax, ay, bx, by, cx, cy) * Side(ax, ay, bx, by, dx, dy);
        var second = Side(cx, cy, dx, dy, ax, ay) * Side(cx, cy, dx, dy, bx, by);

        return first < 0 && second < 0;
    }

    /// <summary>Which side of a span a point falls on.</summary>
    private static double Side(double ax, double ay, double bx, double by, double px, double py) =>
        ((bx - ax) * (py - ay)) - ((by - ay) * (px - ax));

    /// <summary>The Liang–Barsky clip of one boundary on a span, as a parameter range.</summary>
    private static bool Clip(double direction, double distance, ref double entering, ref double leaving)
    {
        if (direction == 0)
        {
            return distance >= 0;
        }

        var crossing = distance / direction;
        if (direction < 0)
        {
            if (crossing > leaving)
            {
                return false;
            }

            entering = Math.Max(entering, crossing);
        }
        else
        {
            if (crossing < entering)
            {
                return false;
            }

            leaving = Math.Min(leaving, crossing);
        }

        return true;
    }
}
