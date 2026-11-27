namespace DbClone.UI.Services.Relationship;

/// <summary>
/// Puts the pointer in its waiting shape while a run the reader asked for is still working (D7).
/// </summary>
/// <remarks>
/// The interface exists so a workflow's promise — a pointer that says "I am still working" — is testable
/// without a window: a cursor that is shown and hidden around a run is the whole contract.
/// </remarks>
internal interface IWaitCursor
{
    /// <summary>Shows the waiting pointer. Calls that overlap share one pointer.</summary>
    void Show();

    /// <summary>Puts the pointer back, unless an overlapping call is still holding it.</summary>
    void Hide();
}
