namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The spacing every placement shares, owned here so that no file reads a constant from a placement the
/// refactor deletes.
/// </summary>
internal static class GraphPlacementConstants
{
    /// <summary>The gap between two columns of cards.</summary>
    public const double ColumnGap = 120;

    /// <summary>The margin around the drawing.</summary>
    public const double Margin = ColumnGap;

    /// <summary>The gap between two unrelated pictures.</summary>
    public const double ComponentGap = 80;

    /// <summary>The gap between two rows of cards, wide enough for a curve to pass between them.</summary>
    public const double RowGap = 60;

    /// <summary>The least empty space a seed leaves between two cards: a lane wide enough for several curves.</summary>
    public const double CardGap = 60;

    /// <summary>How much wider than tall a picture aims to be; a page is wider than tall too.</summary>
    public const double Aspect = 1.6;
}
