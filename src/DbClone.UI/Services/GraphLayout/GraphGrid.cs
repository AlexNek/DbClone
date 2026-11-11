namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The grid the cards are packed on: where each column starts and how wide it is, where each row
/// starts, and the corridor at every edge between them. The router turns in a corridor, so a path
/// passes between two cards instead of across them.
/// </summary>
/// <param name="ColumnLefts">The left edge of each column, in the drawing's own coordinates.</param>
/// <param name="ColumnWidths">The width of each column: the widest card it holds.</param>
/// <param name="RowTops">The top edge of each row. Every row is one card tall.</param>
/// <param name="ColumnCorridors">
/// The vertical corridor at the left of each column, plus one after the last column — a column index
/// is also the index of its left corridor, so a card in column <c>k</c> is flanked by corridors
/// <c>k</c> and <c>k + 1</c>.
/// </param>
/// <param name="RowCorridors">The horizontal corridor above each row, plus one below the last row.</param>
/// <param name="ColumnOfNode">The column each node sits in.</param>
/// <param name="RowOfNode">The row each node sits in.</param>
/// <param name="Width">The drawing's full width, margins included.</param>
/// <param name="Height">The drawing's full height, margins included.</param>
internal sealed record GraphGrid(
    IReadOnlyList<double> ColumnLefts,
    IReadOnlyList<double> ColumnWidths,
    IReadOnlyList<double> RowTops,
    IReadOnlyList<double> ColumnCorridors,
    IReadOnlyList<double> RowCorridors,
    IReadOnlyList<int> ColumnOfNode,
    IReadOnlyList<int> RowOfNode,
    double Width,
    double Height);
