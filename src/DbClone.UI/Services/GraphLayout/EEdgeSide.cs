namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The side of a card an arrow ends on, in the order the border is walked from the card's top-left
/// corner. An end names its side because the side decides which way the arrow has to leave: a line
/// that runs along the border it stands on reads as a stroke drawn over the table.
/// </summary>
internal enum EEdgeSide
{
    Top,
    Right,
    Bottom,
    Left,
}
