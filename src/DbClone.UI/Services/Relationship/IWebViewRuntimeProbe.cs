namespace DbClone.UI.Services.Relationship;

/// <summary>
/// Answers one question: can this machine host a document in the app, or must the browser take it (D4)?
/// </summary>
/// <remarks>
/// The interface exists so the resolver's decision is testable without a browser engine: a probe that
/// says no is the whole difference between the two presenters.
/// </remarks>
internal interface IWebViewRuntimeProbe
{
    /// <summary>Gets whether an in-app runtime can host a document right now.</summary>
    /// <returns><see langword="true"/> when the runtime is installed and usable.</returns>
    bool IsAvailable();
}
