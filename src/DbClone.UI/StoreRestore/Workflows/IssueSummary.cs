using DbClone.Application.Interchange;

namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Turns the issues a store or a restore reported into the one sentence a user reads first.
/// </summary>
/// <remarks>
/// An outcome used to state a count — "Restore failed: 1 issue(s)" — which names nothing a reader can
/// act on, while the reason itself travelled only in the log pane and in a banner that closes. A run
/// that stops before its first stage is exactly where that hurts: nothing is written, no stage reports,
/// and the whole of what happened is the one sentence the product already stated.
/// </remarks>
internal static class IssueSummary
{
    /// <summary>
    /// States the reason for an outcome that did not finish the work, from the issues it reported.
    /// </summary>
    /// <param name="outcome">The opening words of the sentence, e.g. <c>Restore failed</c>.</param>
    /// <param name="issues">The issues the product reported, in the order it reported them.</param>
    /// <returns>
    /// The opening words and the first error's sentence, with how many further reasons followed it.
    /// </returns>
    internal static string ForOutcome(string outcome, IReadOnlyList<InterchangeIssueView> issues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentNullException.ThrowIfNull(issues);

        // An error is the reason a run stopped; a warning beside it explains a part rather than the
        // whole, so it is used only when the run reported no error at all.
        var reasons = Describe(issues, EInterchangeSeverity.Error);
        if (reasons.Count == 0)
        {
            reasons = Describe(issues, severity: null);
        }

        return reasons.Count switch
        {
            0 => $"{outcome}; the run reported no reason.",
            1 => $"{outcome}: {reasons[0]}",
            _ => $"{outcome}: {reasons[0]} (and {reasons.Count - 1} more)",
        };
    }

    private static List<string> Describe(
        IReadOnlyList<InterchangeIssueView> issues,
        EInterchangeSeverity? severity) =>
        [.. issues
            .Where(issue => severity is null || issue.Severity == severity)
            .Select(issue => issue.Sentence)
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))];
}
