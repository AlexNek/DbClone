namespace DbClone.UI.Services.Relationship;

/// <summary>
/// A presenter that hosts the document inside the app. It states one extra fact — whether a window is
/// already open — because a window outlives a runtime that disappears in the middle of a session (D4).
/// </summary>
internal interface IInAppRelationshipPresenter : IRelationshipViewPresenter
{
    /// <summary>Gets whether a window is already open and can take another document.</summary>
    bool HasWindow { get; }
}
