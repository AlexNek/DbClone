using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Places every card in the band its references put it in: a table that references nothing sits in the
/// top band, a table that references it sits in the band below, and within a band the cards are ordered
/// so that each one is drawn under or over the tables it is joined to.
/// </summary>
/// <remarks>
/// <para>
/// The arrangement is read off the graph, never off a fill order. Depth comes from the condensation — a
/// cyclic group is one unit, because its members reach each other and no one of them is above another —
/// and the order inside a band comes from barycentre sweeps: a card moves toward the mean position of
/// its neighbours in the band above, then in the band below, a few times over. That is the crossing
/// reduction. Two constraints between neighbouring bands cannot cross when their ends hold the same
/// relative order in both bands, and the sweeps are what put them in it.
/// </para>
/// <para>
/// Bands are laid on a lattice of columns rather than in free space, and that is a deliberate trade. A
/// corridor free of cards along its whole height is what lets the router take a way between two tables
/// instead of across a third, and what gives an edge's text a corridor wide enough to be printed in.
/// The lattice is not a uniform grid: a column is as wide as the widest card in it, a band is as tall
/// as the group blocks it holds, one unit occupies as many cells as it needs, and columns and rows
/// nothing occupies are dropped before the drawing is measured.
/// </para>
/// <para>
/// Where in that lattice a card stands is decided by two readings of the same graph. The bands are
/// filled in order and slid under the tables above them, which keeps a band a contiguous patch, and
/// the same bands are then aligned after Brandes and Köpf so that a run of tables joined end to end
/// stands in one straight column instead of as a staircase. The two are measured against each other
/// and the cheaper one is drawn, so a straightening that would cost a crossing is never taken.
/// </para>
/// <para>
/// Every tie is settled by a node or unit index, never by hash-set iteration order, so the same model
/// produces the same coordinates on every run and under any culture (T5). Crossing minimisation is a
/// heuristic for fewer crossings, never a minimum: laying a graph out with minimal total edge length
/// and no crossing is NP-hard.
/// </para>
/// </remarks>
internal static class GraphRankedPlacement
{
    /// <summary>
    /// The gap between two columns, which is also the corridor a route may turn in. It is wide enough
    /// for the edge text as well as the line: a label is written across the corridor it sits in, so a
    /// corridor narrower than a label is a corridor whose routes can only ever be drawn unlabelled
    /// (D4, §5.9).
    /// </summary>
    internal const double ColumnGap = 120;

    /// <summary>The gap between two rows, which is also the corridor a route may turn in.</summary>
    internal const double RowGap = 34;

    /// <summary>
    /// The margin around the drawing. It holds the outermost column corridors, which sit half a gap
    /// outside the first and last column, with the same room again beside them — so a route that turns
    /// in the picture's own edge corridor is drawn inside the canvas instead of along its cut edge.
    /// </summary>
    internal const double Margin = ColumnGap;

    /// <summary>How much wider than tall the picture aims to be; a page is wider than tall too.</summary>
    internal const double Aspect = 1.7;

    /// <summary>How many order-then-reorder sweeps a picture is put through.</summary>
    private const int Sweeps = 4;

    /// <summary>How many rounds of trading two neighbours of a band the other way round are tried.</summary>
    private const int Transposes = 4;

    /// <summary>The widest a cyclic group's own block may be, in cells.</summary>
    private const int WidestBlock = 6;

    /// <summary>
    /// How many widths a picture may be measured at before the page's own guess stops being the only
    /// one tried. A wider picture is worth measuring; measuring every column between the guess and the
    /// widest band is not, because the work grows with the square of the picture's arms.
    /// </summary>
    private const int WidestTries = 6;

    /// <summary>
    /// How many rounds of trading two neighbours of a band the other way round are tried once the
    /// picture is placed, which is the repair the measurement itself drives.
    /// </summary>
    private const int Repairs = 3;

    /// <summary>
    /// How many trades a picture may be measured at before it is taken as settled. A trade costs a
    /// whole re-place and a measurement, so the count is bounded and the bound is the same on every
    /// run — a catalog too large to be repaired move by move keeps the arrangement it was drawn at
    /// (T5).
    /// </summary>
    private const int RepairTries = 96;

    /// <summary>Places every card in its band and orders each band.</summary>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="widths">The measured width of every card, in node order.</param>
    /// <returns>One placement per node and the lattice the routes are turned in.</returns>
    public static (NodePlacement[] Placement, GraphGrid Grid) Place(
        CatalogGraphModel model,
        IReadOnlyList<double> widths)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(widths);

        var count = model.Nodes.Count;
        var units = Units(model, out var unitOfNode);
        var neighbours = Neighbourhoods(model, unitOfNode, units.Count);

        var bands = Bands(Rank(model, unitOfNode, units.Count), units.Count);
        Refine(bands, neighbours);

        var best = Search(model, units, neighbours, bands, widths, ColumnCount(count, widths), count);
        best = Settled(model, units, neighbours, bands, best);
        var (columnsOfNode, rowsOfNode, usedColumns, usedRows) = Cells(best, count);

        return Emit(count, widths, columnsOfNode, rowsOfNode, usedColumns, usedRows);
    }

    /// <summary>
    /// The arrangement the picture is drawn from: the page's own width, unless a wider one draws the
    /// whole picture with no arrow over a card it does not join; and, for a picture no width can draw
    /// cleanly, the width that leaves the fewest arrows over a card, then the one that wraps the
    /// fewest bands onto a second line, then the one whose own arrows measure best.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page's own number is a guess from the card count, and a guess one column short is not a
    /// narrower picture: it wraps the last card of a band onto a second line, which puts a table
    /// between a parent and its child and makes two arrows detour round it. Widening is the page's
    /// shape being overridden, so the narrowest width that buys the one thing a width can give — a
    /// picture no arrow has to detour in — is the one drawn.
    /// </para>
    /// <para>
    /// A picture no width can draw cleanly, such as a fan of twenty children reaching one parent,
    /// has no such width to find, and the first one tried used to be drawn whatever it measured. That
    /// is what laid five children of one table into a staircase three rows deep — the guess the page
    /// made was the guess that bent them. Such a picture is now drawn at the width its own arrows
    /// measure best: the fewest arrows over a table first, because that is the one fault a reader
    /// cannot read past; then the fewest bands drawn on two lines, because a wrapped band is the
    /// same fault one row further out — a child standing under its own siblings has to climb past
    /// them; and the shorter of two that draw as many (T5).
    /// </para>
    /// </remarks>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="units">The members of every unit.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <param name="bands">The units of every band, in drawing order.</param>
    /// <param name="widths">The measured width of every card, in node order.</param>
    /// <param name="budget">The column count the page's own aspect asks for.</param>
    /// <param name="count">How many tables the picture holds.</param>
    /// <returns>The arrangement to draw.</returns>
    private static GraphArrangement Search(
        CatalogGraphModel model,
        IReadOnlyList<List<int>> units,
        IReadOnlyList<List<int>> neighbours,
        IReadOnlyList<List<int>> bands,
        IReadOnlyList<double> widths,
        int budget,
        int count)
    {
        GraphArrangement? bent = null;
        var bentBlocked = int.MaxValue;
        var bentWrapped = int.MaxValue;
        var bentCost = double.MaxValue;

        foreach (var columns in Budgets(budget, bands, units, count))
        {
            var shapes = new (int Width, int Height)[units.Count];
            for (var unit = 0; unit < units.Count; unit++)
            {
                shapes[unit] = BlockShape(units[unit].Count, columns);
            }

            var width = Math.Max(columns, shapes.Max(shape => shape.Width));
            var (columnOfNode, rowOfNode, _) = Fill(bands, shapes, units, neighbours, width);
            var seed = new GraphArrangement(
                model, units, shapes, UnitRows(rowOfNode, units), UnitColumns(columnOfNode, units));
            var candidate = Best(neighbours, seed);

            if (candidate.Blocked == 0)
            {
                return candidate.Arrangement;
            }

            var cost = candidate.Arrangement.Cost();
            var wrapped = Wrapped(bands, candidate.Arrangement);
            if (candidate.Blocked < bentBlocked
                || (candidate.Blocked == bentBlocked
                    && (wrapped < bentWrapped || (wrapped == bentWrapped && cost < bentCost))))
            {
                bent = candidate.Arrangement;
                bentBlocked = candidate.Blocked;
                bentWrapped = wrapped;
                bentCost = cost;
            }
        }

        return bent ?? throw new InvalidOperationException("No column budget was tried.");
    }

    /// <summary>
    /// Repairs the arrangement the picture is drawn from, two neighbours of a band at a time, until no
    /// trade draws its own arrows any cheaper. The order a band is drawn in is set before the picture
    /// is measured — by the means of its neighbours, which is a proxy — so the arrangement is re-read
    /// here against the measurement itself: a trade is drawn and kept only when the arrows the picture
    /// already has cost less afterwards, and a round that trades nothing ends the repair (T5, T25).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A band is re-placed in full after a trade, because which of its cards wraps onto the next line
    /// is decided by the order it is filled in: trading the last two cards of a band can be the
    /// difference between a child drawn beside its siblings and one drawn under them, and only the
    /// measurement says which of the two is the better picture.
    /// </para>
    /// <para>
    /// The repair never makes a picture worse: the arrangement it starts from is the one to beat, and
    /// a trade that measures the same is put back as it was, so a picture that was already settled is
    /// drawn exactly as it was placed.
    /// </para>
    /// </remarks>
    /// <param name="model">The analyzed graph whose arrows are measured.</param>
    /// <param name="units">The members of every unit.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <param name="bands">The units of every band, in drawing order.</param>
    /// <param name="winner">The arrangement the width search chose.</param>
    /// <returns>The arrangement to draw.</returns>
    private static GraphArrangement Settled(
        CatalogGraphModel model,
        IReadOnlyList<List<int>> units,
        IReadOnlyList<List<int>> neighbours,
        IReadOnlyList<List<int>> bands,
        GraphArrangement winner)
    {
        var shapes = new (int Width, int Height)[units.Count];
        for (var unit = 0; unit < units.Count; unit++)
        {
            shapes[unit] = winner.ShapeOf(unit);
        }

        var width = Math.Max(1, winner.Columns());
        var best = winner;
        var bestCost = winner.Cost();
        var tries = 0;

        GraphArrangement? Attempt()
        {
            if (tries >= RepairTries)
            {
                return null;
            }

            tries++;
            var (columnOfNode, rowOfNode, _) = Fill(bands, shapes, units, neighbours, width);
            var seed = new GraphArrangement(
                model, units, shapes, UnitRows(rowOfNode, units), UnitColumns(columnOfNode, units));

            return Aligned(seed, neighbours);
        }

        for (var round = 0; round < Repairs && tries < RepairTries; round++)
        {
            var improved = false;

            foreach (var band in bands)
            {
                // Two neighbours of a line the other way round: the order is the only thing a fit
                // cannot change, so it is what the measurement is asked to settle.
                for (var index = 0; index + 1 < band.Count; index++)
                {
                    (band[index], band[index + 1]) = (band[index + 1], band[index]);
                    var moved = Attempt();

                    if (moved is not null && moved.Cost() < bestCost)
                    {
                        best = moved;
                        bestCost = moved.Cost();
                        improved = true;
                    }
                    else
                    {
                        (band[index], band[index + 1]) = (band[index + 1], band[index]);
                    }
                }

                // The band as a ring: every card in turn moved to the front, which is the one move
                // that changes where the band's line breaks without changing the band's order.
                for (var offset = 1; offset < band.Count; offset++)
                {
                    var front = band[0];
                    band.RemoveAt(0);
                    band.Add(front);

                    var moved = Attempt();
                    if (moved is not null && moved.Cost() < bestCost)
                    {
                        best = moved;
                        bestCost = moved.Cost();
                        improved = true;
                    }
                    else
                    {
                        band.RemoveAt(band.Count - 1);
                        band.Insert(0, front);
                    }
                }
            }

            if (!improved)
            {
                break;
            }
        }

        return best;
    }

    /// <summary>
    /// How many bands the arrangement draws on more than one line. A band that wraps puts one of its
    /// cards under its own siblings instead of beside them, so that card's arrow has to climb past a
    /// row of cards it does not join — a fault of the width, which is why it is read before the cost
    /// and after the arrows themselves (T25).
    /// </summary>
    /// <param name="bands">The units of every band, in drawing order.</param>
    /// <param name="arrangement">The arrangement to measure.</param>
    /// <returns>How many bands stand on more than one row.</returns>
    private static int Wrapped(IReadOnlyList<List<int>> bands, GraphArrangement arrangement)
    {
        var wrapped = 0;

        foreach (var band in bands)
        {
            var first = arrangement.RowOfUnit[band[0]];
            foreach (var unit in band)
            {
                if (arrangement.RowOfUnit[unit] != first)
                {
                    wrapped++;
                    break;
                }
            }
        }

        return wrapped;
    }

    /// <summary>
    /// The column budgets to try: the one the page asks for, the two above it, and the width of the
    /// widest band — never wider than one column per table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page's number is a floor rather than a guess to be second-guessed. It is solved for a
    /// rectangle of one cell per table, and bands can only ever want more rows than such a rectangle
    /// has — so the picture it describes is at least as tall as the guess assumed, and the column it is
    /// short of is the one that makes it wide enough for its own height. A narrower picture is never
    /// drawn, because nothing the guess missed would ask for one (T5).
    /// </para>
    /// <para>
    /// The widest band is the last width worth trying, because a width at least as wide as the widest
    /// band is the one width at which no band is bent onto a second line. Without it a picture whose
    /// children outnumber the guess could never be drawn unbent, however much its own arrows would
    /// gain by it.
    /// </para>
    /// </remarks>
    /// <param name="budget">The column count the page's own aspect asks for.</param>
    /// <param name="bands">The units of every band, in drawing order.</param>
    /// <param name="units">The members of every unit.</param>
    /// <param name="count">How many tables the picture holds.</param>
    /// <returns>The widths to measure, at most <see cref="WidestTries"/> of them.</returns>
    private static IEnumerable<int> Budgets(
        int budget,
        IReadOnlyList<List<int>> bands,
        IReadOnlyList<List<int>> units,
        int count)
    {
        var widest = bands.Count == 0
            ? Math.Min(count, budget + 2)
            : bands.Max(band => band.Sum(unit => units[unit].Count));
        var low = Math.Max(1, Math.Min(budget, count));
        var top = Math.Max(low, Math.Min(count, Math.Max(Math.Min(budget + 2, count), widest)));
        var step = Math.Max(1, (int)Math.Ceiling((top - low + 1) / (double)WidestTries));

        for (var columns = low; columns < top; columns += step)
        {
            yield return columns;
        }

        yield return top;
    }

    /// <summary>
    /// The arrangement whose own arrows measure best. The ranked arrangement is the first candidate,
    /// so measuring can only improve a picture: a candidate wins by crossing fewer arrows or drawing
    /// them shorter, and a tie keeps the one already standing (pass 11).
    /// </summary>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <param name="seed">The ranked arrangement, already placed.</param>
    /// <returns>The arrangement to draw and how many arrows it blocks.</returns>
    private static (GraphArrangement Arrangement, int Blocked) Best(
        IReadOnlyList<List<int>> neighbours,
        GraphArrangement seed)
    {
        var best = seed;
        var bestCost = seed.Cost();

        foreach (var candidate in Candidates(neighbours, seed))
        {
            var cost = candidate.Cost();
            if (cost < bestCost)
            {
                best = candidate;
                bestCost = cost;
            }
        }

        return (best, best.Measure().Blocked);
    }

    /// <summary>
    /// The arrangements tried against the ranked one: its bands realigned toward the tables above
    /// them, then toward the tables below them, then again toward the tables above — the same three
    /// starting from the arrangement a stranded leaf is pulled down into, and then the straight
    /// columns Brandes and Köpf align the bands into, read from either end and from either side.
    /// </summary>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <param name="seed">The ranked arrangement.</param>
    /// <returns>Every candidate to measure.</returns>
    private static IEnumerable<GraphArrangement> Candidates(
        IReadOnlyList<List<int>> neighbours,
        GraphArrangement seed)
    {
        foreach (var start in new[] { seed, Relaxed(seed, neighbours) })
        {
            var down = Aligned(start, neighbours);
            yield return down;

            var up = Aligned(down, neighbours);
            yield return up;

            yield return Aligned(up, neighbours);
        }

        // A band is placed by its own ask, which keeps it a patch but cannot straighten a chain: the
        // child of a table two columns out is drawn two columns out. The blocks are the answer to
        // that, and they are offered after the aligned candidates so a picture whose arrows measure
        // the same keeps the arrangement it had.
        foreach (var down in new[] { true, false })
        {
            foreach (var right in new[] { true, false })
            {
                yield return seed.WithColumns(GraphBlockAlignment.Columns(seed, neighbours, down, right));
            }
        }
    }

    /// <summary>
    /// Re-places every band so its units stand under or over the units they are joined to, taking the
    /// order from the band it already has. The band is fitted to the tables on one side of it — the
    /// side it is approaching from — and the fit keeps their relative order, so no arrow is made to
    /// cross another by the alignment itself.
    /// </summary>
    /// <remarks>
    /// A band is placed by its own ask, never by the picture's budget: the fit adds columns when the
    /// tables it must reach stand further out than the budget guessed, which is what a clamped offset
    /// could not do (pass 11).
    /// </remarks>
    /// <param name="arrangement">The arrangement to realign, which it copies.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <returns>The realigned arrangement.</returns>
    private static GraphArrangement Aligned(GraphArrangement arrangement, IReadOnlyList<List<int>> neighbours)
    {
        var next = arrangement.Copy();
        Realign(next, neighbours, down: true);
        Realign(next, neighbours, down: false);

        return next;
    }

    /// <summary>Realigns every row toward the rows on one side of it.</summary>
    /// <param name="arrangement">The arrangement to change in place.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <param name="down">Whether the rows are read from the top or from the bottom.</param>
    private static void Realign(GraphArrangement arrangement, IReadOnlyList<List<int>> neighbours, bool down)
    {
        var rows = new SortedSet<int>();
        for (var unit = 0; unit < arrangement.UnitCount; unit++)
        {
            rows.Add(arrangement.RowOfUnit[unit]);
        }

        var order = down ? [.. rows] : rows.Reverse().ToList();

        foreach (var row in order)
        {
            var band = new List<int>();
            for (var unit = 0; unit < arrangement.UnitCount; unit++)
            {
                if (arrangement.RowOfUnit[unit] == row)
                {
                    band.Add(unit);
                }
            }

            var asks = new List<(int Unit, double Centre)>(band.Count);
            foreach (var unit in band)
            {
                var sum = 0.0;
                var seen = 0;

                foreach (var neighbour in neighbours[unit])
                {
                    var placed = down
                        ? arrangement.RowOfUnit[neighbour] < row
                        : arrangement.RowOfUnit[neighbour] > row;

                    if (!placed)
                    {
                        continue;
                    }

                    sum += arrangement.CentreX(neighbour);
                    seen++;
                }

                asks.Add((unit, seen == 0 ? arrangement.CentreX(unit) : sum / seen));
            }

            // The band is ordered by where its tables ask to stand, not by where it happened to
            // stand: it was ordered while its parents were still somewhere else, and an order that
            // no longer follows them strands a child across the picture — the child then has to be
            // slid past the whole band, so the slide is what gets dropped (pass 11). A tie keeps
            // the band's own order, and a band whose units all ask for one table stays packed.
            asks.Sort((first, second) =>
            {
                var byAsk = first.Centre.CompareTo(second.Centre);
                if (byAsk != 0)
                {
                    return byAsk;
                }

                var byColumn = arrangement.ColumnOfUnit[first.Unit].CompareTo(arrangement.ColumnOfUnit[second.Unit]);
                return byColumn != 0 ? byColumn : first.Unit.CompareTo(second.Unit);
            });

            var units = new List<int>(band.Count);
            var centres = new double[band.Count];
            for (var index = 0; index < asks.Count; index++)
            {
                units.Add(asks[index].Unit);
                centres[index] = asks[index].Centre;
            }

            Fit(units, centres, arrangement);
        }
    }

    /// <summary>
    /// Gives every unit of one band the column nearest its own ask while keeping the band's order,
    /// which is the least-squares fit of a non-decreasing sequence (isotonic regression), rounded to
    /// cell columns and opened out so two units never share a column.
    /// </summary>
    /// <param name="band">The units of the band, in drawing order.</param>
    /// <param name="centres">The centre each unit asks for, in cells.</param>
    /// <param name="arrangement">The arrangement to change in place.</param>
    private static void Fit(List<int> band, IReadOnlyList<double> centres, GraphArrangement arrangement)
    {
        var wanted = new double[band.Count];
        var offsets = new int[band.Count];
        var offset = 0;

        for (var index = 0; index < band.Count; index++)
        {
            var unit = band[index];
            var (width, _) = arrangement.ShapeOf(unit);

            offsets[index] = offset;
            wanted[index] = centres[index] - (width / 2.0) - offset;
            offset += width;
        }

        var fitted = Isotonic(wanted);
        var left = int.MinValue;

        for (var index = 0; index < band.Count; index++)
        {
            var unit = band[index];
            var (width, _) = arrangement.ShapeOf(unit);
            var column = (int)Math.Round(fitted[index], MidpointRounding.AwayFromZero) + offsets[index];

            if (left > int.MinValue)
            {
                column = Math.Max(column, left);
            }

            arrangement.ColumnOfUnit[unit] = column;
            left = column + width;
        }
    }

    /// <summary>The non-decreasing sequence that is closest to the given values, in least squares.</summary>
    /// <param name="values">The values to fit.</param>
    /// <returns>One fitted value per input, in the same order.</returns>
    private static double[] Isotonic(IReadOnlyList<double> values)
    {
        var sums = new double[values.Count];
        var counts = new int[values.Count];
        var blocks = 0;

        foreach (var value in values)
        {
            sums[blocks] = value;
            counts[blocks] = 1;
            blocks++;

            while (blocks > 1 && (sums[blocks - 2] / counts[blocks - 2]) > (sums[blocks - 1] / counts[blocks - 1]))
            {
                sums[blocks - 2] += sums[blocks - 1];
                counts[blocks - 2] += counts[blocks - 1];
                blocks--;
            }
        }

        var fitted = new double[values.Count];
        var index = 0;

        for (var block = 0; block < blocks; block++)
        {
            var mean = sums[block] / counts[block];
            for (var step = 0; step < counts[block]; step++)
            {
                fitted[index++] = mean;
            }
        }

        return fitted;
    }

    /// <summary>
    /// Pulls a table down beside the tables it is joined to when every one of them stands below it:
    /// a leaf whose whole neighbourhood is deep belongs beside that neighbourhood, and drawn in the
    /// top band it would be stranded across the picture from it (pass 11).
    /// </summary>
    /// <param name="arrangement">The arrangement to relax, which it copies.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <returns>The relaxed arrangement.</returns>
    private static GraphArrangement Relaxed(GraphArrangement arrangement, IReadOnlyList<List<int>> neighbours)
    {
        var next = arrangement.Copy();
        var order = Enumerable.Range(0, next.UnitCount)
            .OrderBy(unit => next.RowOfUnit[unit])
            .ThenBy(unit => unit);

        foreach (var unit in order)
        {
            if (neighbours[unit].Count == 0)
            {
                continue;
            }

            var deepest = int.MaxValue;
            var stranded = true;

            foreach (var neighbour in neighbours[unit])
            {
                stranded &= next.RowOfUnit[neighbour] > next.RowOfUnit[unit];
                deepest = Math.Min(deepest, next.RowOfUnit[neighbour]);
            }

            // The unit keeps its whole block above the nearest table it is joined to, so an arrow is
            // never turned upside down by the pull: a three-row block whose neighbour stands one row
            // below it has nowhere to go, while a single card one row above its neighbour has.
            var target = deepest - next.ShapeOf(unit).Height;
            if (!stranded || target <= next.RowOfUnit[unit])
            {
                continue;
            }

            if (!FreeColumn(next, unit, target, out var column))
            {
                continue;
            }

            next.RowOfUnit[unit] = target;
            next.ColumnOfUnit[unit] = column;
        }

        return next;
    }

    /// <summary>The free column in a row closest to where a unit already stands.</summary>
    /// <param name="arrangement">The arrangement to look in.</param>
    /// <param name="unit">The unit looking for a column.</param>
    /// <param name="row">The row it wants to stand in.</param>
    /// <param name="column">The column it can take.</param>
    /// <returns>Whether a free column was found.</returns>
    private static bool FreeColumn(GraphArrangement arrangement, int unit, int row, out int column)
    {
        var wanted = arrangement.ColumnOfUnit[unit];
        var reach = arrangement.Columns() + 4;

        for (var step = 0; step <= reach; step++)
        {
            if (arrangement.Fits(unit, row, wanted + step, unit))
            {
                column = wanted + step;
                return true;
            }

            if (step == 0)
            {
                continue;
            }

            if (arrangement.Fits(unit, row, wanted - step, unit))
            {
                column = wanted - step;
                return true;
            }
        }

        column = wanted;
        return false;
    }

    /// <summary>The top row of every unit, read from the cell of its first member.</summary>
    /// <param name="rowOfNode">The row of every node.</param>
    /// <param name="units">The members of every unit.</param>
    /// <returns>One row per unit.</returns>
    private static int[] UnitRows(IReadOnlyList<int> rowOfNode, IReadOnlyList<List<int>> units)
    {
        var rows = new int[units.Count];
        for (var unit = 0; unit < units.Count; unit++)
        {
            rows[unit] = rowOfNode[units[unit][0]];
        }

        return rows;
    }

    /// <summary>The left column of every unit, read from the cell of its first member.</summary>
    /// <param name="columnOfNode">The column of every node.</param>
    /// <param name="units">The members of every unit.</param>
    /// <returns>One column per unit.</returns>
    private static int[] UnitColumns(IReadOnlyList<int> columnOfNode, IReadOnlyList<List<int>> units)
    {
        var columns = new int[units.Count];
        for (var unit = 0; unit < units.Count; unit++)
        {
            columns[unit] = columnOfNode[units[unit][0]];
        }

        return columns;
    }

    /// <summary>Reads an arrangement back as one cell per node, counting from the picture's own origin.</summary>
    /// <param name="arrangement">The arrangement to read.</param>
    /// <param name="nodeCount">How many nodes the picture holds.</param>
    /// <returns>The cell of every node, and how many columns and rows the arrangement spans.</returns>
    private static (int[] ColumnOfNode, int[] RowOfNode, int Columns, int Rows) Cells(
        GraphArrangement arrangement,
        int nodeCount)
    {
        var columnOfNode = new int[nodeCount];
        var rowOfNode = new int[nodeCount];
        var left = int.MaxValue;
        var right = int.MinValue;
        var top = int.MaxValue;
        var bottom = int.MinValue;

        for (var unit = 0; unit < arrangement.UnitCount; unit++)
        {
            var (width, height) = arrangement.ShapeOf(unit);
            var members = arrangement.MembersOf(unit);

            left = Math.Min(left, arrangement.ColumnOfUnit[unit]);
            right = Math.Max(right, arrangement.ColumnOfUnit[unit] + width);
            top = Math.Min(top, arrangement.RowOfUnit[unit]);
            bottom = Math.Max(bottom, arrangement.RowOfUnit[unit] + height);

            for (var member = 0; member < members.Count; member++)
            {
                columnOfNode[members[member]] = arrangement.ColumnOfUnit[unit] + (member % width);
                rowOfNode[members[member]] = arrangement.RowOfUnit[unit] + (member / width);
            }
        }

        // The alignment may push a band left of the lattice's origin, and a relaxation may pull one
        // down past the row a band used to start at. Emit counts from zero, so the cells are moved
        // back into the picture rather than lost.
        for (var node = 0; node < nodeCount; node++)
        {
            columnOfNode[node] -= left;
            rowOfNode[node] -= top;
        }

        return (columnOfNode, rowOfNode, right - left, bottom - top);
    }

    /// <summary>
    /// The units the picture is drawn from: every cyclic group of two or more tables as one unit, and
    /// every other table as a unit of its own. A group is one unit because its members reach each other
    /// — an arrow runs both ways around it — so no one member can be drawn above another.
    /// </summary>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="unitOfNode">For each node, the unit that holds it.</param>
    /// <returns>The members of every unit, in unit order.</returns>
    private static List<List<int>> Units(CatalogGraphModel model, out int[] unitOfNode)
    {
        var count = model.Nodes.Count;
        var claimed = new bool[count];
        var units = new List<List<int>>();

        foreach (var group in model.CyclicGroups)
        {
            if (group.MemberNodeIndexes.Count < 2)
            {
                continue;
            }

            var members = new List<int>(group.MemberNodeIndexes);
            members.Sort();
            foreach (var member in members)
            {
                claimed[member] = true;
            }

            units.Add(members);
        }

        foreach (var node in model.Nodes)
        {
            if (!claimed[node.Index])
            {
                units.Add([node.Index]);
            }
        }

        units.Sort(static (first, second) => first[0].CompareTo(second[0]));

        unitOfNode = new int[count];
        for (var unit = 0; unit < units.Count; unit++)
        {
            foreach (var member in units[unit])
            {
                unitOfNode[member] = unit;
            }
        }

        return units;
    }

    /// <summary>
    /// The units each unit is joined to, in either direction and in unit order. Being drawn together is
    /// about the constraint, not about which end stores the value, so the order sweeps read the union.
    /// </summary>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="unitOfNode">For each node, the unit that holds it.</param>
    /// <param name="unitCount">How many units the graph has.</param>
    /// <returns>One neighbour list per unit, distinct and ascending.</returns>
    private static List<int>[] Neighbourhoods(CatalogGraphModel model, IReadOnlyList<int> unitOfNode, int unitCount)
    {
        var sets = new HashSet<int>[unitCount];
        for (var unit = 0; unit < unitCount; unit++)
        {
            sets[unit] = [];
        }

        foreach (var edge in model.Edges)
        {
            var child = unitOfNode[edge.ChildIndex];
            var parent = unitOfNode[edge.ParentIndex];
            if (child == parent)
            {
                continue;
            }

            sets[child].Add(parent);
            sets[parent].Add(child);
        }

        var neighbours = new List<int>[unitCount];
        for (var unit = 0; unit < unitCount; unit++)
        {
            neighbours[unit] = [.. sets[unit].Order()];
        }

        return neighbours;
    }

    /// <summary>
    /// The band every unit belongs to: how many references deep it is. A unit nothing points away from
    /// is at the top; otherwise it is one band below the deepest unit it references. The pass is a
    /// topological walk of the condensation, so a cycle is one unit and cannot send itself deeper, and a
    /// table joined to nothing at all is drawn in a band of its own below the rest rather than being
    /// interleaved with the tables that are joined.
    /// </summary>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="unitOfNode">For each node, the unit that holds it.</param>
    /// <param name="unitCount">How many units the graph has.</param>
    /// <returns>One band per unit.</returns>
    private static int[] Rank(CatalogGraphModel model, IReadOnlyList<int> unitOfNode, int unitCount)
    {
        var targets = new List<int>[unitCount];
        var sources = new List<int>[unitCount];
        for (var unit = 0; unit < unitCount; unit++)
        {
            targets[unit] = [];
            sources[unit] = [];
        }

        foreach (var edge in model.Edges)
        {
            var child = unitOfNode[edge.ChildIndex];
            var parent = unitOfNode[edge.ParentIndex];
            if (child != parent)
            {
                targets[child].Add(parent);
            }
        }

        var pending = new int[unitCount];
        var ready = new SortedSet<int>();
        for (var unit = 0; unit < unitCount; unit++)
        {
            targets[unit] = [.. targets[unit].Distinct().Order()];
            pending[unit] = targets[unit].Count;

            foreach (var target in targets[unit])
            {
                sources[target].Add(unit);
            }

            if (pending[unit] == 0)
            {
                ready.Add(unit);
            }
        }

        var rank = new int[unitCount];
        var deepest = new int[unitCount];

        while (ready.Count > 0)
        {
            var unit = ready.Min;
            ready.Remove(unit);
            rank[unit] = deepest[unit];

            foreach (var source in sources[unit])
            {
                deepest[source] = Math.Max(deepest[source], rank[unit] + 1);
                pending[source]--;
                if (pending[source] == 0)
                {
                    ready.Add(source);
                }
            }
        }

        var floor = 0;
        for (var unit = 0; unit < unitCount; unit++)
        {
            floor = Math.Max(floor, rank[unit]);
        }

        var loners = floor + 1;
        for (var unit = 0; unit < unitCount; unit++)
        {
            if (targets[unit].Count == 0 && sources[unit].Count == 0)
            {
                rank[unit] = loners;
            }
        }

        return rank;
    }

    /// <summary>The units of every band, band by band from the top, in unit order, empty bands dropped.</summary>
    /// <param name="rank">The band each unit belongs to.</param>
    /// <param name="unitCount">How many units the graph has.</param>
    /// <returns>One list of units per occupied band.</returns>
    private static List<List<int>> Bands(IReadOnlyList<int> rank, int unitCount)
    {
        var deepest = 0;
        for (var unit = 0; unit < unitCount; unit++)
        {
            deepest = Math.Max(deepest, rank[unit]);
        }

        var bands = new List<List<int>>(deepest + 1);
        for (var band = 0; band <= deepest; band++)
        {
            bands.Add([]);
        }

        for (var unit = 0; unit < unitCount; unit++)
        {
            bands[rank[unit]].Add(unit);
        }

        return [.. bands.Where(band => band.Count > 0)];
    }

    /// <summary>
    /// Puts the bands in the order that draws the fewest crossing arrows: a sweep from the top reads
    /// each band against the one above and sorts it by its neighbours' mean position, then a sweep from
    /// the bottom does the same against the band below, a few times over so the two readings settle,
    /// and a last pass trades two neighbours of a band whenever that crosses fewer arrows still.
    /// </summary>
    /// <param name="bands">The units of every band, reordered in place.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    private static void Refine(IReadOnlyList<List<int>> bands, IReadOnlyList<List<int>> neighbours)
    {
        for (var sweep = 0; sweep < Sweeps; sweep++)
        {
            for (var band = 1; band < bands.Count; band++)
            {
                Order(bands[band], neighbours, Positions(bands[band - 1]));
            }

            for (var band = bands.Count - 2; band >= 0; band--)
            {
                Order(bands[band], neighbours, Positions(bands[band + 1]));
            }
        }

        Transpose(bands, neighbours);
    }

    /// <summary>
    /// Trades two neighbours of a band the other way round whenever that crosses fewer arrows with the
    /// bands beside it. The mean position a band is ordered by is a guess that two cards of nearly the
    /// same mean can sit the wrong way round; this pass reads the arrows themselves, so it settles what
    /// the means could not. A swap is kept only when the crossings of the two bands it touches fall, so
    /// a band is never reordered for nothing and the picture can only improve.
    /// </summary>
    /// <param name="bands">The units of every band, reordered in place.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    private static void Transpose(IReadOnlyList<List<int>> bands, IReadOnlyList<List<int>> neighbours)
    {
        for (var round = 0; round < Transposes; round++)
        {
            var improved = false;

            for (var band = 0; band < bands.Count; band++)
            {
                var units = bands[band];

                for (var index = 0; index + 1 < units.Count; index++)
                {
                    var before = Crossings(bands, neighbours, band);
                    (units[index], units[index + 1]) = (units[index + 1], units[index]);

                    if (Crossings(bands, neighbours, band) < before)
                    {
                        improved = true;
                    }
                    else
                    {
                        (units[index], units[index + 1]) = (units[index + 1], units[index]);
                    }
                }
            }

            if (!improved)
            {
                return;
            }
        }
    }

    /// <summary>How many arrows cross each other around one band.</summary>
    /// <param name="bands">The units of every band, in drawing order.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <param name="band">The band to measure.</param>
    /// <returns>The crossings with the band above and with the band below, added.</returns>
    private static int Crossings(
        IReadOnlyList<List<int>> bands,
        IReadOnlyList<List<int>> neighbours,
        int band)
    {
        var crossings = 0;

        if (band > 0)
        {
            crossings += Inversions(bands[band - 1], bands[band], neighbours);
        }

        if (band + 1 < bands.Count)
        {
            crossings += Inversions(bands[band], bands[band + 1], neighbours);
        }

        return crossings;
    }

    /// <summary>
    /// How many arrows between two neighbouring bands cross each other: an arrow is read as the seat of
    /// the card it joins in the band below, and two arrows cross when their seats fall in the opposite
    /// order from the two cards they start at.
    /// </summary>
    /// <param name="upper">The units of the band above, in drawing order.</param>
    /// <param name="lower">The units of the band below, in drawing order.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <returns>The number of crossing arrow pairs.</returns>
    private static int Inversions(
        IReadOnlyList<int> upper,
        IReadOnlyList<int> lower,
        IReadOnlyList<List<int>> neighbours)
    {
        var seats = new Dictionary<int, int>(lower.Count);
        for (var seat = 0; seat < lower.Count; seat++)
        {
            seats[lower[seat]] = seat;
        }

        var crossings = 0;
        var read = new List<int>(upper.Count * 2);

        foreach (var unit in upper)
        {
            var ends = new List<int>(neighbours[unit].Count);

            foreach (var neighbour in neighbours[unit])
            {
                if (seats.TryGetValue(neighbour, out var seat))
                {
                    ends.Add(seat);
                }
            }

            ends.Sort();

            foreach (var seat in ends)
            {
                // Every arrow read earlier whose end stands further along the band below crosses this
                // one, because its start stands before this arrow's start and its end after its end.
                foreach (var earlier in read)
                {
                    if (earlier > seat)
                    {
                        crossings++;
                    }
                }
            }

            read.AddRange(ends);
            read.Sort();
        }

        return crossings;
    }

    /// <summary>The position of every unit of one band, for the band next to it to read.</summary>
    /// <param name="band">The units, in the order they will be drawn.</param>
    /// <returns>One position per unit of that band.</returns>
    private static Dictionary<int, int> Positions(IReadOnlyList<int> band)
    {
        var positions = new Dictionary<int, int>(band.Count);
        for (var position = 0; position < band.Count; position++)
        {
            positions[band[position]] = position;
        }

        return positions;
    }

    /// <summary>
    /// Sorts one band by the mean position of each unit's neighbours in an adjacent band. A unit with no
    /// neighbour there keeps the position it had, so a table nothing joins is never shuffled for no
    /// reason, and every tie keeps the order the band already had, so the result never depends on the
    /// sort's own stability.
    /// </summary>
    /// <param name="band">The units of one band, reordered in place.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <param name="adjacent">The position of every unit of the band being read.</param>
    private static void Order(
        List<int> band,
        IReadOnlyList<List<int>> neighbours,
        IReadOnlyDictionary<int, int> adjacent)
    {
        if (band.Count < 2)
        {
            return;
        }

        var order = new List<(int Unit, double Barycentre, int Was)>(band.Count);
        for (var position = 0; position < band.Count; position++)
        {
            var unit = band[position];
            var sum = 0.0;
            var seen = 0;

            foreach (var neighbour in neighbours[unit])
            {
                if (!adjacent.TryGetValue(neighbour, out var other))
                {
                    continue;
                }

                sum += other;
                seen++;
            }

            order.Add((unit, seen == 0 ? position : sum / seen, position));
        }

        order.Sort(static (first, second) => first.Barycentre != second.Barycentre
            ? first.Barycentre.CompareTo(second.Barycentre)
            : first.Was.CompareTo(second.Was));

        band.Clear();
        foreach (var entry in order)
        {
            band.Add(entry.Unit);
        }
    }

    /// <summary>
    /// The number of columns the picture aims for, solved from the aspect ratio the page wants:
    /// <c>columns ≈ sqrt(nodes × aspect × (cardHeight + rowGap) / (meanWidth + columnGap))</c>.
    /// </summary>
    /// <param name="count">How many tables the picture holds.</param>
    /// <param name="widths">The measured width of every card.</param>
    /// <returns>The column budget before the group blocks have their say.</returns>
    private static int ColumnCount(int count, IReadOnlyList<double> widths)
    {
        if (count <= 1)
        {
            return 1;
        }

        var mean = widths.Average();
        var ideal = Aspect * count * (GraphCard.Height + RowGap) / (mean + ColumnGap);
        var columns = (int)Math.Round(Math.Sqrt(ideal), MidpointRounding.AwayFromZero);

        return Math.Clamp(columns, 1, count);
    }

    /// <summary>
    /// The shape a cyclic group's block takes: the rectangle that wastes the fewest cells, then the one
    /// closest to square and the wider of two equals. A small group therefore leaves no hole for another
    /// table to be drawn in, and a large one stays a patch rather than a bar (T11).
    /// </summary>
    /// <param name="members">How many tables the group holds.</param>
    /// <param name="columns">The column budget the block must fit in.</param>
    /// <returns>The block's width and height, in cells.</returns>
    private static (int Width, int Height) BlockShape(int members, int columns)
    {
        var width = 1;
        var height = members;
        var waste = int.MaxValue;
        var balance = int.MaxValue;

        for (var candidate = 1; candidate <= Math.Max(1, Math.Min(columns, Math.Min(members, WidestBlock))); candidate++)
        {
            var rows = (int)Math.Ceiling((double)members / candidate);
            var extra = (candidate * rows) - members;
            var spread = Math.Abs(candidate - rows);
            var better = extra < waste
                || (extra == waste && (spread < balance || (spread == balance && candidate >= height)));

            if (!better)
            {
                continue;
            }

            width = candidate;
            height = rows;
            waste = extra;
            balance = spread;
        }

        return (width, height);
    }

    /// <summary>
    /// Turns the ordered bands into cells: each unit is given the next free columns of its band, wrapped
    /// on to a new line of that band when the column budget runs out, and every band is then slid
    /// sideways as a whole until its tables stand under the tables they are joined to.
    /// </summary>
    /// <remarks>
    /// The band's own order fixes the sequence; the slide fixes the alignment. Sliding a band rather
    /// than granting each unit its own column keeps the band a contiguous patch — which is what the
    /// lattice's corridors need — while a band placed dead centre would draw every child one column to
    /// the side of its parent and pay for it with an extra turn in the route. A band whose tables have
    /// nothing above them has no vote and stays centred.
    /// </remarks>
    /// <param name="bands">The units of every band, in drawing order.</param>
    /// <param name="shapes">The cell rectangle each unit occupies.</param>
    /// <param name="units">The members of every unit.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <param name="columns">The column budget.</param>
    /// <returns>The cell of every node, and how many rows the picture used.</returns>
    private static (int[] ColumnOfNode, int[] RowOfNode, int Rows) Fill(
        IReadOnlyList<List<int>> bands,
        IReadOnlyList<(int Width, int Height)> shapes,
        IReadOnlyList<List<int>> units,
        IReadOnlyList<List<int>> neighbours,
        int columns)
    {
        var count = units.Sum(members => members.Count);
        var columnOfNode = new int[count];
        var rowOfNode = new int[count];
        var columnOfUnit = new int[units.Count];
        Array.Fill(columnOfUnit, -1);
        var row = 0;

        foreach (var band in bands)
        {
            var placed = new List<(int Row, int Column)>(band.Count);
            var cursor = 0;
            var lineTop = 0;
            var lineHeight = 0;
            var width = 0;

            foreach (var unit in band)
            {
                var (unitWidth, unitHeight) = shapes[unit];
                if (cursor > 0 && cursor + unitWidth > columns)
                {
                    lineTop += lineHeight;
                    cursor = 0;
                    lineHeight = 0;
                }

                placed.Add((lineTop, cursor));
                cursor += unitWidth;
                width = Math.Max(width, cursor);
                lineHeight = Math.Max(lineHeight, unitHeight);
            }

            var height = lineTop + lineHeight;
            var offset = Slide(band, placed, shapes, neighbours, columnOfUnit, columns, width);

            for (var index = 0; index < band.Count; index++)
            {
                columnOfUnit[band[index]] = offset + placed[index].Column;
            }

            for (var index = 0; index < band.Count; index++)
            {
                var unit = band[index];
                var (unitWidth, _) = shapes[unit];
                var (unitRow, unitColumn) = placed[index];
                var members = units[unit];

                for (var member = 0; member < members.Count; member++)
                {
                    columnOfNode[members[member]] = offset + unitColumn + (member % unitWidth);
                    rowOfNode[members[member]] = row + unitRow + (member / unitWidth);
                }
            }

            row += height;
        }

        return (columnOfNode, rowOfNode, row);
    }

    /// <summary>
    /// How far a whole band is slid sideways from the budget's left edge: the mean of what each of its
    /// tables asks for, where a table asks to stand over the tables above it that it is joined to. A
    /// band whose tables are joined to nothing above it asks for nothing and is centred instead, which
    /// is also what keeps an unconnected table on its own in the middle of the picture.
    /// </summary>
    /// <param name="band">The band's units, in order.</param>
    /// <param name="placed">Where each unit sits inside the band before any slide.</param>
    /// <param name="shapes">The cell rectangle each unit occupies.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <param name="columnOfUnit">The left column of every unit already placed, or -1.</param>
    /// <param name="columns">The column budget.</param>
    /// <param name="width">How many columns the band occupies before any slide.</param>
    /// <returns>The left column the band is drawn at.</returns>
    private static int Slide(
        List<int> band,
        List<(int Row, int Column)> placed,
        IReadOnlyList<(int Width, int Height)> shapes,
        IReadOnlyList<List<int>> neighbours,
        IReadOnlyList<int> columnOfUnit,
        int columns,
        int width)
    {
        var total = 0.0;
        var votes = 0;

        for (var index = 0; index < band.Count; index++)
        {
            var unit = band[index];
            var (unitWidth, _) = shapes[unit];

            var sum = 0.0;
            var seen = 0;
            foreach (var neighbour in neighbours[unit])
            {
                if (columnOfUnit[neighbour] >= 0)
                {
                    sum += columnOfUnit[neighbour];
                    seen++;
                }
            }

            if (seen == 0)
            {
                continue;
            }

            var column = placed[index].Column;
            var wanted = (int)Math.Round(sum / seen, MidpointRounding.AwayFromZero) - column;

            // A unit may ask for a place the budget does not have; the ask is trimmed to the edge it
            // overran, so one extreme table cannot drag the whole band off the picture.
            total += Math.Clamp(wanted, -column, columns - unitWidth - column);
            votes++;
        }

        var offset = votes == 0 ? (columns - width) / 2 : (int)Math.Round(total / votes, MidpointRounding.AwayFromZero);
        return Math.Clamp(offset, 0, Math.Max(0, columns - width));
    }

    /// <summary>
    /// Turns the cells into coordinates: a column is as wide as its widest card, the cards are centred
    /// in it, one card per row, and columns or rows no card occupies are dropped so the picture has no
    /// empty band.
    /// </summary>
    /// <param name="count">How many nodes there are.</param>
    /// <param name="widths">The measured width of every card, in node order.</param>
    /// <param name="columnOfNode">The cell column of every node.</param>
    /// <param name="rowOfNode">The cell row of every node.</param>
    /// <param name="columns">How many cell columns the fill used.</param>
    /// <param name="rows">How many cell rows the fill used.</param>
    /// <returns>One placement per node and the lattice the routes are turned in.</returns>
    private static (NodePlacement[] Placement, GraphGrid Grid) Emit(
        int count,
        IReadOnlyList<double> widths,
        IReadOnlyList<int> columnOfNode,
        IReadOnlyList<int> rowOfNode,
        int columns,
        int rows)
    {
        var occupiedColumn = new bool[Math.Max(1, columns)];
        var occupiedRow = new bool[Math.Max(1, rows)];

        for (var node = 0; node < count; node++)
        {
            occupiedColumn[columnOfNode[node]] = true;
            occupiedRow[rowOfNode[node]] = true;
        }

        var columnIndexOf = Compact(occupiedColumn);
        var rowIndexOf = Compact(occupiedRow);
        var columnCount = columnIndexOf.Count(index => index >= 0);
        var rowCount = rowIndexOf.Count(index => index >= 0);

        var columnWidths = new double[columnCount];
        Array.Fill(columnWidths, GraphCard.MinWidth);
        for (var node = 0; node < count; node++)
        {
            var column = columnIndexOf[columnOfNode[node]];
            columnWidths[column] = Math.Max(columnWidths[column], widths[node]);
        }

        var columnLefts = new double[columnCount];
        var x = Margin;
        for (var column = 0; column < columnCount; column++)
        {
            columnLefts[column] = x;
            x += columnWidths[column] + ColumnGap;
        }

        var rowTops = new double[rowCount];
        var y = Margin;
        for (var row = 0; row < rowCount; row++)
        {
            rowTops[row] = y;
            y += GraphCard.Height + RowGap;
        }

        var placement = new NodePlacement[count];
        var placedColumn = new int[count];
        var placedRow = new int[count];

        for (var node = 0; node < count; node++)
        {
            var column = columnIndexOf[columnOfNode[node]];
            var row = rowIndexOf[rowOfNode[node]];
            placedColumn[node] = column;
            placedRow[node] = row;

            var left = columnLefts[column] + ((columnWidths[column] - widths[node]) / 2);
            placement[node] = new NodePlacement(node, new GraphPoint(left, rowTops[row]), widths[node], GraphCard.Height);
        }

        var columnCorridors = new double[columnCount + 1];
        for (var corridor = 0; corridor < columnCorridors.Length; corridor++)
        {
            columnCorridors[corridor] = corridor == 0
                ? columnLefts[0] - (ColumnGap / 2)
                : columnLefts[corridor - 1] + columnWidths[corridor - 1] + (ColumnGap / 2);
        }

        var rowCorridors = new double[rowCount + 1];
        for (var corridor = 0; corridor < rowCorridors.Length; corridor++)
        {
            rowCorridors[corridor] = corridor == 0
                ? rowTops[0] - (RowGap / 2)
                : rowTops[corridor - 1] + GraphCard.Height + (RowGap / 2);
        }

        var width = columnLefts[^1] + columnWidths[^1] + Margin;
        var height = rowTops[^1] + GraphCard.Height + Margin;

        return (placement, new GraphGrid(
            columnLefts,
            columnWidths,
            rowTops,
            columnCorridors,
            rowCorridors,
            placedColumn,
            placedRow,
            width,
            height));
    }

    /// <summary>Numbers the occupied entries from zero and marks the rest with -1.</summary>
    /// <param name="occupied">Which entries a card occupies.</param>
    /// <returns>For each entry, its number in the compacted lattice, or -1.</returns>
    private static int[] Compact(IReadOnlyList<bool> occupied)
    {
        var indexOf = new int[occupied.Count];
        var next = 0;

        for (var index = 0; index < occupied.Count; index++)
        {
            indexOf[index] = occupied[index] ? next++ : -1;
        }

        return indexOf;
    }
}
