using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Draws runs of tables joined one to another in straight columns, after Brandes and Köpf: every
/// table is aligned with one table of the band beside it, the alignments are chained into blocks,
/// and every block is given a single column.
/// </summary>
/// <remarks>
/// <para>
/// Alignment is a choice, never a vote. A table follows the median of the tables it is joined to in
/// the band beside it, only one table may follow the same neighbour, and the followers of a band are
/// taken in the order that band is read — so two blocks cannot be made to cross by the alignment
/// itself. Four biases are tried, the bands read from the top or from the bottom and each band read
/// from the left or from the right, because which neighbour is "the median" depends on the side a
/// band is read from; the arrangement that measures best is the one kept.
/// </para>
/// <para>
/// A block is a chain of tables, one per band, and it stands in one column: that is what turns a run
/// of five tables joined end to end into one straight line instead of a staircase. No two members of
/// a block share a band, so the only thing a column has to survive is the rest of its band; the
/// columns are therefore pushed apart band by band, and a push moves a whole block, which is why
/// straightness survives it.
/// </para>
/// <para>
/// Rows are not this class's business: it keeps the rows it was handed and replaces only the columns,
/// so its answer can be measured against the arrangement it came from. Every decision is made from
/// indexes, seats and sorted keys, never from iteration order, so the same catalog draws the same
/// picture on every run (T5).
/// </para>
/// </remarks>
internal static class GraphBlockAlignment
{
    /// <summary>
    /// Replaces the columns of an arrangement with the columns its blocks ask for.
    /// </summary>
    /// <param name="arrangement">The arrangement whose rows and band order are kept.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <param name="down">
    /// Whether the alignments are read from the top band down, or from the bottom band up.
    /// </param>
    /// <param name="right">
    /// Whether each band is read from its left edge, or from its right edge.
    /// </param>
    /// <returns>The left column of every unit.</returns>
    public static int[] Columns(
        GraphArrangement arrangement,
        IReadOnlyList<List<int>> neighbours,
        bool down,
        bool right)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(neighbours);

        var (layerOf, layers) = Layers(arrangement);
        var followOf = Align(arrangement, layerOf, layers, neighbours, down, right);
        var blockOf = Blocks(followOf);
        var columnOfBlock = Compaction(arrangement, layers, blockOf);

        var columnOfUnit = new int[arrangement.UnitCount];
        for (var unit = 0; unit < columnOfUnit.Length; unit++)
        {
            columnOfUnit[unit] = columnOfBlock[blockOf[unit]];
        }

        return columnOfUnit;
    }

    /// <summary>
    /// The bands of the arrangement, top to bottom, with every unit's band and the order the cards of
    /// a band are drawn in. A band is one row of cards, which is what a table aligned to another has
    /// to share a column with — a band that wrapped onto a second line is therefore two bands here,
    /// not one.
    /// </summary>
    /// <param name="arrangement">The arrangement to read.</param>
    /// <returns>Every unit's band, and the units of every band in drawing order.</returns>
    private static (int[] LayerOf, List<List<int>> Layers) Layers(GraphArrangement arrangement)
    {
        var rows = new SortedSet<int>();
        for (var unit = 0; unit < arrangement.UnitCount; unit++)
        {
            rows.Add(arrangement.RowOfUnit[unit]);
        }

        var layerOfRow = new Dictionary<int, int>(rows.Count);
        var layers = new List<List<int>>(rows.Count);
        foreach (var row in rows)
        {
            layerOfRow[row] = layers.Count;
            layers.Add([]);
        }

        var layerOf = new int[arrangement.UnitCount];
        for (var unit = 0; unit < layerOf.Length; unit++)
        {
            var layer = layerOfRow[arrangement.RowOfUnit[unit]];
            layerOf[unit] = layer;
            layers[layer].Add(unit);
        }

        foreach (var layer in layers)
        {
            layer.Sort((first, second) =>
            {
                var byColumn = arrangement.ColumnOfUnit[first].CompareTo(arrangement.ColumnOfUnit[second]);
                return byColumn != 0 ? byColumn : first.CompareTo(second);
            });
        }

        return (layerOf, layers);
    }

    /// <summary>
    /// Gives every table at most one table to stand with: the median of its neighbours in the band
    /// beside it, if that neighbour is still free and stands beyond the chain already taken on that
    /// side of the band. Following the band in one direction and never reaching back is what keeps
    /// two blocks from crossing.
    /// </summary>
    /// <param name="arrangement">The arrangement whose bands are read.</param>
    /// <param name="layerOf">The band of every unit.</param>
    /// <param name="layers">The units of every band, in drawing order.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <param name="down">Whether the alignments are read from the top band down.</param>
    /// <param name="right">Whether each band is read from its left edge.</param>
    /// <returns>For every unit, the unit it stands with one band over, or -1.</returns>
    private static int[] Align(
        GraphArrangement arrangement,
        IReadOnlyList<int> layerOf,
        IReadOnlyList<List<int>> layers,
        IReadOnlyList<List<int>> neighbours,
        bool down,
        bool right)
    {
        var followOf = new int[arrangement.UnitCount];
        Array.Fill(followOf, -1);
        var followed = new bool[arrangement.UnitCount];

        for (var step = 0; step < layers.Count; step++)
        {
            var layer = down ? step : layers.Count - 1 - step;
            var beside = down ? layer + 1 : layer - 1;
            if (beside < 0 || beside >= layers.Count)
            {
                continue;
            }

            var seats = Seats(layers[beside]);
            var reach = right ? -1 : int.MaxValue;
            var band = layers[layer];

            for (var index = 0; index < band.Count; index++)
            {
                var unit = band[right ? index : band.Count - 1 - index];
                var choice = Choice(unit, neighbours, layerOf, beside, seats, followed, reach, right);

                if (choice < 0)
                {
                    continue;
                }

                followOf[unit] = choice;
                followed[choice] = true;
                reach = seats[choice];
            }
        }

        return followOf;
    }

    /// <summary>The seat of every unit of one band: its place when the band is read from its left edge.</summary>
    /// <param name="band">The units of the band.</param>
    /// <returns>One seat per unit.</returns>
    private static Dictionary<int, int> Seats(IReadOnlyList<int> band)
    {
        var seats = new Dictionary<int, int>(band.Count);
        for (var seat = 0; seat < band.Count; seat++)
        {
            seats[band[seat]] = seat;
        }

        return seats;
    }

    /// <summary>
    /// The neighbour one table stands with: the median of the free neighbours it is joined to in the
    /// band beside it, taken among those that stand beyond the chain already taken.
    /// </summary>
    /// <param name="unit">The table looking for a neighbour to stand with.</param>
    /// <param name="neighbours">The units each unit is joined to.</param>
    /// <param name="layerOf">The band of every unit.</param>
    /// <param name="beside">The band the neighbour must belong to.</param>
    /// <param name="seats">The seat of every unit of that band.</param>
    /// <param name="followed">Which units already have a table standing with them.</param>
    /// <param name="reach">The seat of the last neighbour taken, which this one must stand beyond.</param>
    /// <param name="right">Whether the band is being read from its left edge.</param>
    /// <returns>The neighbour to stand with, or -1 when there is none.</returns>
    private static int Choice(
        int unit,
        IReadOnlyList<List<int>> neighbours,
        IReadOnlyList<int> layerOf,
        int beside,
        IReadOnlyDictionary<int, int> seats,
        IReadOnlyList<bool> followed,
        int reach,
        bool right)
    {
        var choices = new List<(int Seat, int Unit)>(neighbours[unit].Count);

        foreach (var neighbour in neighbours[unit])
        {
            if (layerOf[neighbour] != beside || followed[neighbour])
            {
                continue;
            }

            var seat = seats[neighbour];

            // A chain already taken on this side of the band may not be crossed: the follower of a
            // table read later must stand further along than the follower taken before it.
            if (right ? seat <= reach : seat >= reach)
            {
                continue;
            }

            choices.Add((seat, neighbour));
        }

        if (choices.Count == 0)
        {
            return -1;
        }

        choices.Sort(static (first, second) =>
            first.Seat != second.Seat ? first.Seat.CompareTo(second.Seat) : first.Unit.CompareTo(second.Unit));

        return choices[(choices.Count - 1) / 2].Unit;
    }

    /// <summary>Numbers the blocks the alignments form: two tables share a number when a chain joins them.</summary>
    /// <param name="followOf">For every unit, the unit it stands with one band over, or -1.</param>
    /// <returns>The block of every unit, numbered in unit order.</returns>
    private static int[] Blocks(IReadOnlyList<int> followOf)
    {
        var parent = new int[followOf.Count];
        for (var unit = 0; unit < parent.Length; unit++)
        {
            parent[unit] = unit;
        }

        int Root(int unit)
        {
            while (parent[unit] != unit)
            {
                parent[unit] = parent[parent[unit]];
                unit = parent[unit];
            }

            return unit;
        }

        for (var unit = 0; unit < followOf.Count; unit++)
        {
            if (followOf[unit] < 0)
            {
                continue;
            }

            var first = Root(unit);
            var second = Root(followOf[unit]);

            // The smaller unit index is the block's own index, so the numbering never depends on the
            // order the links were walked (T5).
            if (first != second)
            {
                parent[Math.Max(first, second)] = Math.Min(first, second);
            }
        }

        var blockOf = new int[followOf.Count];
        var numbered = new Dictionary<int, int>(followOf.Count);

        for (var unit = 0; unit < followOf.Count; unit++)
        {
            var root = Root(unit);
            if (!numbered.TryGetValue(root, out var block))
            {
                block = numbered.Count;
                numbered[root] = block;
            }

            blockOf[unit] = block;
        }

        return blockOf;
    }

    /// <summary>
    /// Turns the blocks into columns: every block starts where its members would stand if their bands
    /// were packed from the left edge, and each band then pushes the blocks that would share a cell
    /// further right. A push moves the whole block, so the chain stays one column.
    /// </summary>
    /// <param name="arrangement">The arrangement being placed.</param>
    /// <param name="layers">The units of every band, in drawing order.</param>
    /// <param name="blockOf">The block of every unit.</param>
    /// <returns>The left column of every block.</returns>
    private static int[] Compaction(
        GraphArrangement arrangement,
        IReadOnlyList<List<int>> layers,
        IReadOnlyList<int> blockOf)
    {
        var count = blockOf.Count == 0 ? 0 : blockOf.Max() + 1;
        var column = new int[count];

        if (count == 0)
        {
            return column;
        }

        var wanted = new List<double>[count];
        for (var block = 0; block < count; block++)
        {
            wanted[block] = [];
        }

        foreach (var layer in layers)
        {
            var x = 0.0;
            foreach (var unit in layer)
            {
                wanted[blockOf[unit]].Add(x + (arrangement.ShapeOf(unit).Width / 2.0));
                x += arrangement.ShapeOf(unit).Width;
            }
        }

        for (var block = 0; block < count; block++)
        {
            var centres = wanted[block];
            centres.Sort();
            column[block] = (int)Math.Round(centres[(centres.Count - 1) / 2], MidpointRounding.AwayFromZero);
        }

        // Every pass either leaves the columns alone or moves one further right, and no column can
        // pass the width of the picture, so the loop ends; each pass is one walk over the cards.
        while (Push(arrangement, layers, blockOf, column))
        {
        }

        return column;
    }

    /// <summary>
    /// Pushes every block that would share a cell with the block before it in its band, and says
    /// whether any block moved. The cards of a band are walked in drawing order, so the order the band
    /// is drawn in is the order the cells are dealt out in.
    /// </summary>
    /// <param name="arrangement">The arrangement being placed.</param>
    /// <param name="layers">The units of every band, in drawing order.</param>
    /// <param name="blockOf">The block of every unit.</param>
    /// <param name="column">The left column of every block, moved in place.</param>
    /// <returns>Whether any block was pushed.</returns>
    private static bool Push(
        GraphArrangement arrangement,
        IReadOnlyList<List<int>> layers,
        IReadOnlyList<int> blockOf,
        int[] column)
    {
        var pushed = false;

        foreach (var layer in layers)
        {
            var cursor = int.MinValue;

            foreach (var unit in layer)
            {
                var block = blockOf[unit];

                if (cursor > int.MinValue && column[block] < cursor)
                {
                    column[block] = cursor;
                    pushed = true;
                }

                cursor = column[block] + arrangement.ShapeOf(unit).Width;
            }
        }

        return pushed;
    }
}
