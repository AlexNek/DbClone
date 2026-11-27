namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The band of page a relation's printed columns takes, and what it means for two of them to meet. The
/// document reveals this text when the reader selects a table or points at an arrow, so the ink is part of
/// the picture the layout is answerable for: two labels whose bands overlap read as one relation with a
/// garbled name, which is the same failure as a stroke a card covers.
/// </summary>
/// <remarks>
/// The figures follow the document's own <c>.elabel</c> rule — 10px text, weighted 600 when it is revealed,
/// centered on its anchor and carried on a white halo 3px wide — and were read off the page a browser paints:
/// a label's <c>getBBox()</c> is sixteen pixels tall and about five and a half wide per character. The width is
/// kept a little larger, the way <see cref="GraphCard"/> does, because the layout is not a text engine and
/// over-estimating only costs a place it did not have to take. The band deliberately stays under the pitch two
/// ports are dealt at (<see cref="GraphGeometryContract.MinimumPortStep"/>): that is what lets the lanes of a
/// parallel family, which are one port step apart, carry one label each a line below the next.
/// </remarks>
/// <param name="Child">The table the relation leaves.</param>
/// <param name="Parent">The table it names.</param>
/// <param name="Left">The smallest x the printed text reaches.</param>
/// <param name="Top">The smallest y the printed text reaches.</param>
/// <param name="Right">The largest x the printed text reaches.</param>
/// <param name="Bottom">The largest y the printed text reaches.</param>
internal readonly record struct GraphLabelInk(
    int Child,
    int Parent,
    double Left,
    double Top,
    double Right,
    double Bottom)
{
    /// <summary>The width one character of a label takes at the size the document prints it.</summary>
    internal const double CharWidth = 6;

    /// <summary>The band one line of that text takes on the page, which is the line box a browser measures.</summary>
    internal const double Height = 16;

    /// <summary>The leading a reader needs between two lines of that text to count two of them.</summary>
    /// <remarks>
    /// This is what <see cref="GraphLabelPlacement"/> spaces its staggered places by. It is deliberately not
    /// added to <see cref="Meets"/>: the band is already the whole line box the browser gives the text, and
    /// asking for a gutter on top of it would demand more than the page can offer — two lanes are dealt no
    /// farther apart than <see cref="GraphGeometryContract.MinimumPortStep"/>, and a test that rejected that
    /// stack would reject the only arrangement that makes a parallel family readable.
    /// </remarks>
    internal const double InkGap = GraphGeometryContract.MinimumVisibleStrokeGap;

    /// <summary>The width a printed label takes on the page.</summary>
    /// <param name="text">The columns the relation states.</param>
    /// <returns>The ink width.</returns>
    internal static double WidthOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Length * CharWidth;
    }

    /// <summary>The band the text occupies when the document centers it on its anchor.</summary>
    /// <param name="anchor">The point the label is written at, which is the middle of its own line.</param>
    /// <param name="text">The columns the relation states.</param>
    /// <param name="child">The table the relation leaves.</param>
    /// <param name="parent">The table it names.</param>
    /// <returns>The band.</returns>
    internal static GraphLabelInk Around(GraphPoint anchor, string text, int child, int parent)
    {
        ArgumentNullException.ThrowIfNull(text);

        var half = WidthOf(text) / 2;

        return new GraphLabelInk(
            child,
            parent,
            anchor.X - half,
            anchor.Y - (Height / 2),
            anchor.X + half,
            anchor.Y + (Height / 2));
    }

    /// <summary>Whether this label and another print on one place.</summary>
    /// <param name="other">The other label's band.</param>
    /// <returns>Whether a reader sees one garbled line instead of two.</returns>
    internal bool Meets(GraphLabelInk other) =>
        Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;

    /// <summary>Whether selecting one of the two tables reveals both labels at once.</summary>
    /// <param name="other">The other label's band.</param>
    /// <returns>Whether the two belong to a set the reader reads together.</returns>
    internal bool RevealedWith(GraphLabelInk other) =>
        Child == other.Child || Child == other.Parent || Parent == other.Child || Parent == other.Parent;

    /// <summary>How much page two labels share, which is the part of a constraint no reader can get back.</summary>
    /// <param name="other">The other label's band.</param>
    /// <returns>The overlapping area; zero when the two bands stand apart.</returns>
    internal double Overlap(GraphLabelInk other)
    {
        var width = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        var height = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);

        return width <= 0 || height <= 0 ? 0 : width * height;
    }

    /// <summary>Whether the band lies over a card, whose own text is what the label would fight.</summary>
    /// <param name="card">The card.</param>
    /// <returns>Whether the two inks share the page.</returns>
    internal bool LiesOver(NodePlacement card)
    {
        ArgumentNullException.ThrowIfNull(card);

        return Left < card.Right && Right > card.TopLeft.X && Top < card.Bottom && Bottom > card.TopLeft.Y;
    }
}
