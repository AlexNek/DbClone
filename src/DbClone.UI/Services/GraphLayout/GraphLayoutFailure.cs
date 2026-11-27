namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Why a layout request produced no picture: the kind, what every seed that was tried reported, and what the
/// search cost. It is handed on instead of an empty or partial set of coordinates, so the workflow says what
/// happened rather than drawing a picture that is missing relations.
/// </summary>
/// <param name="Kind">Whether the caller stopped the request, the search found nothing, or time ran out.</param>
/// <param name="Reports">What happened to every seed of every component, in the order they were tried.</param>
/// <param name="GeometryEvaluations">The candidate routes the search flattened and checked before giving up.</param>
internal sealed record GraphLayoutFailure(
    ELayoutFailureKind Kind,
    IReadOnlyList<GraphSeedReport> Reports,
    int GeometryEvaluations)
{
    /// <summary>Gets the relations that could not be routed, in edge order, over every seed.</summary>
    public IReadOnlyList<RouteFailure> Failures =>
        [.. Reports.SelectMany(report => report.Failures).OrderBy(failure => failure.EdgeIndex)];

    /// <summary>Gets one sentence the workflow can print beside the picture it could not replace.</summary>
    public string Message
    {
        get
        {
            var first = Failures.FirstOrDefault();
            return Kind switch
            {
                ELayoutFailureKind.Cancelled =>
                    "the arrangement was cancelled before a complete picture was reached.",
                ELayoutFailureKind.LayoutTimeLimit =>
                    "the arrangement ran out of time and no complete picture was reached.",
                _ when first is null =>
                    "no arrangement could draw every relation: the search budget ended before a complete picture was reached.",
                _ =>
                    $"no arrangement could draw every relation without a hard failure (relation {first.EdgeIndex}: {first.Kind}).",
            };
        }
    }
}
