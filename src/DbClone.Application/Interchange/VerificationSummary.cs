namespace DbClone.Application.Interchange;

/// <summary>
/// Verification verdict for a catalog — hashes and counts checked by the product's verifier.
/// Separate from restoration validation; a catalog can verify and still produce validation findings.
/// </summary>
public sealed record VerificationSummary(
    bool Passed,
    IReadOnlyList<InterchangeIssueView> Issues);
