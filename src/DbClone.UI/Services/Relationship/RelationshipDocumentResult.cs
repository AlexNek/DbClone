using DbClone.UI.Services.GraphLayout;

namespace DbClone.UI.Services.Relationship;

/// <summary>
/// The answer of the document stage: the whole document, or the layout failure that stopped it. A caller never
/// receives a document that claims a valid picture it does not hold, which is the contract the relationship
/// view's own error path is written for.
/// </summary>
/// <param name="Document">The document; <see langword="null"/> when the picture could not be arranged.</param>
/// <param name="Failure">Why no document was built; <see langword="null"/> when one was.</param>
/// <param name="IsDegraded">Whether the picture is the cheapest complete arrangement the watchdog allowed.</param>
/// <param name="IsRough">
/// Whether the picture is the arrangement of last resort: every table and relation is drawn, but no stroke
/// was proven to clear the cards and strokes in its way.
/// </param>
/// <param name="Facts">What the arrangement decided, when the search measured it.</param>
internal sealed record RelationshipDocumentResult(
    RelationshipDocument? Document,
    GraphLayoutFailure? Failure,
    bool IsDegraded,
    bool IsRough = false,
    GraphLayoutFacts? Facts = null)
{
    /// <summary>Gets whether a document was built.</summary>
    public bool IsSuccess => Document is not null;

    /// <summary>A built document.</summary>
    /// <param name="document">The document.</param>
    /// <param name="isDegraded">Whether the layout search was cut short, so the picture is the cheap one.</param>
    /// <param name="isRough">Whether the picture is the arrangement of last resort.</param>
    /// <param name="facts">What the arrangement decided, when the search measured it.</param>
    /// <returns>The successful result.</returns>
    public static RelationshipDocumentResult Success(
        RelationshipDocument document,
        bool isDegraded = false,
        bool isRough = false,
        GraphLayoutFacts? facts = null) =>
        new(document, null, isDegraded, isRough, facts);

    /// <summary>No document, and the reason.</summary>
    /// <param name="failure">What ended the layout without a picture.</param>
    /// <returns>The failed result.</returns>
    public static RelationshipDocumentResult Failed(GraphLayoutFailure failure) => new(null, failure, false);

    /// <summary>
    /// The document, for a caller that cannot carry a failure — a presenter delegate whose only word is the
    /// one it throws. The failure itself is the message, so the presenter prints what happened instead of a
    /// second-hand sentence.
    /// </summary>
    /// <returns>The document.</returns>
    /// <exception cref="InvalidOperationException">No document was built.</exception>
    public RelationshipDocument OrThrow() =>
        Document ?? throw new InvalidOperationException(Failure?.Message ?? "The relationship document could not be built.");
}
