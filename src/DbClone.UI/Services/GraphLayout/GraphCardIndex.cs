namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// A uniform grid over the cards of one layout, so a route's collision check asks only for the cards near
/// the stretch it is walking instead of walking every card of the picture for every stretch. Each card is
/// filed in the cells its box grown by the clearance covers, which is the widest grown box a check reads
/// (a card the route joins is shrunk instead), so a query answers with a superset of what any clearance
/// can hit and the caller applies the exact one.
/// </summary>
internal sealed class GraphCardIndex
{
    private readonly IReadOnlyList<NodePlacement> _cards;
    private readonly Dictionary<(long, long), List<int>> _cells = [];
    private readonly double _cell;

    /// <summary>Indexes the cards, each filed with the room a route must keep from it.</summary>
    /// <param name="cards">The cards, by node index.</param>
    /// <param name="margin">The clearance grown around every card before it is filed.</param>
    public GraphCardIndex(IReadOnlyList<NodePlacement> cards, double margin)
    {
        ArgumentNullException.ThrowIfNull(cards);

        _cards = cards;

        var widest = 0.0;
        foreach (var card in cards)
        {
            widest = Math.Max(widest, Math.Max(card.Width, card.Height));
        }

        _cell = Math.Max(1, widest + margin);

        for (var position = 0; position < cards.Count; position++)
        {
            var card = cards[position];
            var (firstColumn, firstRow, lastColumn, lastRow) = Cells(
                card.TopLeft.X - margin,
                card.TopLeft.Y - margin,
                card.Right + margin,
                card.Bottom + margin);

            for (var column = firstColumn; column <= lastColumn; column++)
            {
                for (var row = firstRow; row <= lastRow; row++)
                {
                    if (!_cells.TryGetValue((column, row), out var members))
                    {
                        _cells[(column, row)] = members = [];
                    }

                    members.Add(position);
                }
            }
        }
    }

    /// <summary>The cards whose grown box may meet the given box, in ascending node order.</summary>
    /// <param name="left">The query box's smallest x.</param>
    /// <param name="top">The query box's smallest y.</param>
    /// <param name="right">The query box's largest x.</param>
    /// <param name="bottom">The query box's largest y.</param>
    /// <returns>The positions of the cards that may meet it.</returns>
    public List<int> Near(double left, double top, double right, double bottom)
    {
        var (firstColumn, firstRow, lastColumn, lastRow) = Cells(left, top, right, bottom);
        var found = new SortedSet<int>();

        for (var column = firstColumn; column <= lastColumn; column++)
        {
            for (var row = firstRow; row <= lastRow; row++)
            {
                if (_cells.TryGetValue((column, row), out var members))
                {
                    found.UnionWith(members);
                }
            }
        }

        // A card can be filed in several cells; the caller reads the card's own box, so a card is named once.
        return found.Where(position => MayMeet(_cards[position], left, top, right, bottom)).ToList();
    }

    private static bool MayMeet(NodePlacement card, double left, double top, double right, double bottom) =>
        card.Right >= left && card.TopLeft.X <= right && card.Bottom >= top && card.TopLeft.Y <= bottom;

    private (long FirstColumn, long FirstRow, long LastColumn, long LastRow) Cells(
        double left,
        double top,
        double right,
        double bottom) =>
        ((long)Math.Floor(left / _cell), (long)Math.Floor(top / _cell), (long)Math.Floor(right / _cell), (long)Math.Floor(bottom / _cell));
}
