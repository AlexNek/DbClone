using System.Windows.Input;

namespace DbClone.UI.Services.Relationship;

/// <summary>
/// The application-wide waiting pointer (D7). A workflow asks for it on whichever thread its own work is
/// running on, and the change is handed to the thread that owns the window, because the pointer is the
/// window manager's business and touching it from elsewhere is not allowed.
/// </summary>
/// <remarks>
/// Overlapping runs share one pointer and the last one to finish puts it back, so a run that ends while
/// another is still working cannot leave the reader with a spinning arrow that never stops. A change is
/// queued rather than waited for: a pointer that arrives a moment late is still a pointer, and a workflow
/// blocked on its own window is not.
/// </remarks>
internal sealed class WaitCursorService : IWaitCursor
{
    private int _held;

    /// <inheritdoc />
    public void Show() => OnUiThread(static self =>
    {
        if (++self._held == 1)
        {
            Mouse.OverrideCursor = Cursors.Wait;
        }
    }, this);

    /// <inheritdoc />
    public void Hide() => OnUiThread(static self =>
    {
        if (self._held == 0)
        {
            return;
        }

        if (--self._held == 0)
        {
            Mouse.OverrideCursor = null;
        }
    }, this);

    /// <summary>Runs one pointer change on the thread that owns the window, or not at all when none does.</summary>
    private static void OnUiThread(Action<WaitCursorService> change, WaitCursorService self)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // No application, no window, no pointer to change: a unit test that runs a workflow is the
            // only reader of this branch, and the run it wraps is what the test is about.
            return;
        }

        if (dispatcher.CheckAccess())
        {
            change(self);
            return;
        }

        dispatcher.BeginInvoke(change, self);
    }
}
