namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The answer of the layout stage: a complete, valid picture, or the explicit reason there is none. It is
/// never a partial one — a relation that could not be routed takes the whole request with it, so no caller
/// can draw a picture that is missing arrows.
/// </summary>
internal sealed class GraphLayoutResult
{
    private GraphLayoutResult(
        CatalogGraphLayout? layout,
        GraphLayoutFailure? failure,
        bool isDegraded,
        bool isRough,
        GraphLayoutFacts? facts,
        bool wasTimeSliced)
    {
        if ((layout is null) == (failure is null))
        {
            throw new ArgumentException("Exactly one layout result value must be supplied.");
        }

        Layout = layout;
        Failure = failure;
        IsDegraded = isDegraded;
        IsRough = isRough;
        Facts = facts;
        WasTimeSliced = wasTimeSliced;
    }

    /// <summary>Gets the finished picture; <see langword="null"/> when there is none.</summary>
    public CatalogGraphLayout? Layout { get; }

    /// <summary>Gets why no picture was drawn; <see langword="null"/> when one was.</summary>
    public GraphLayoutFailure? Failure { get; }

    /// <summary>
    /// Gets what the arrangement decided for this picture — the seed that won, its crossings, its page and
    /// what every other seed did — so a picture can be judged from the picture that was drawn.
    /// <see langword="null"/> when the search chose nothing to measure.
    /// </summary>
    public GraphLayoutFacts? Facts { get; }

    /// <summary>
    /// Gets whether the picture is the best arrangement the remaining work could reach rather than the one the
    /// search would have chosen: the elapsed-time watchdog stopped the refining. A degraded picture is complete
    /// and valid — it is drawn with the crossings and the reading order its seed happened to give — and the
    /// workflow says so beside it.
    /// </summary>
    public bool IsDegraded { get; }

    /// <summary>
    /// Gets whether the picture is the arrangement of last resort rather than one the search chose: every card is
    /// placed and every relation is drawn, but no stroke was proven to clear the cards and strokes in its way. It
    /// is drawn with a warning saying so, because a picture the reader can judge beats a window that shows nothing.
    /// </summary>
    public bool IsRough { get; }

    /// <summary>
    /// Gets whether one of the search's clock-sliced passes — the refining search or the post-selection beauty
    /// pass — gave up mid-work because its own slice expired, while the request as a whole stayed inside
    /// <see cref="GraphSearchBudget.MaximumElapsedLayoutTime"/>. The picture is complete and valid; what the
    /// search did not reach depends on how busy the machine was, so its counts and its coordinates are the
    /// answer of an unfinished search rather than the proof a finished one gives.
    /// </summary>
    public bool WasTimeSliced { get; }

    /// <summary>Gets whether a picture was drawn.</summary>
    public bool IsSuccess => Layout is not null;

    /// <summary>A finished picture.</summary>
    /// <param name="layout">The coordinates and routes.</param>
    /// <param name="isDegraded">Whether the watchdog cut the refining short and the picture is the cheap one.</param>
    /// <param name="isRough">Whether the picture is the arrangement of last resort.</param>
    /// <param name="facts">What the arrangement decided, when the search measured it.</param>
    /// <param name="wasTimeSliced">Whether a clock slice cut a pass short before it finished its work.</param>
    /// <returns>The successful result.</returns>
    public static GraphLayoutResult Success(
        CatalogGraphLayout layout,
        bool isDegraded = false,
        bool isRough = false,
        GraphLayoutFacts? facts = null,
        bool wasTimeSliced = false) =>
        new(layout, null, isDegraded, isRough, facts, wasTimeSliced);

    /// <summary>No picture, and the reason.</summary>
    /// <param name="failure">What ended the request without a valid layout.</param>
    /// <returns>The failed result.</returns>
    public static GraphLayoutResult Failed(GraphLayoutFailure failure) => new(null, failure, false, false, null, false);
}
