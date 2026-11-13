using System.Windows.Threading;

namespace DbClone.UI.Threading;

/// <summary>
/// The one place that moves work onto the thread that owns the application's windows.
/// </summary>
/// <remarks>
/// WPF refuses a change to a bound collection from any other thread — "This type of CollectionView
/// does not support changes to its SourceCollection from a thread different from the Dispatcher
/// thread" — and a background continuation reaches this application's UI state on several paths: a
/// workflow's log line written after a step finished its file work, a progress report, an update
/// download. Callers state the work and this type decides where it runs, so no caller reaches for
/// <see cref="System.Windows.Application.Current"/> itself or repeats the dispatcher check.
/// </remarks>
internal static class UiDispatcher
{
    /// <summary>
    /// Runs the work on the UI thread, or now when the caller is already on it.
    /// </summary>
    /// <param name="action">The work to run.</param>
    /// <remarks>
    /// Runs inline when the application has no dispatcher — a test, or a window that does not exist
    /// yet — because there is then no other thread to hand the work to.
    /// </remarks>
    public static void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Current;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    /// <summary>
    /// Runs the work on the UI thread and completes when it has run.
    /// </summary>
    /// <param name="action">The work to run.</param>
    /// <returns>A task that completes once the work has run.</returns>
    /// <exception cref="InvalidOperationException">The application has no dispatcher — it is shutting down.</exception>
    public static Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Current ?? throw NoApplication();

        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    /// <summary>
    /// Runs the asynchronous work on the UI thread and completes when that work has finished.
    /// </summary>
    /// <param name="action">The asynchronous work to run.</param>
    /// <returns>A task that completes once the work it hands back has finished.</returns>
    /// <exception cref="InvalidOperationException">The application has no dispatcher — it is shutting down.</exception>
    public static Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Current ?? throw NoApplication();

        return dispatcher.CheckAccess()
            ? action()
            : dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private static Dispatcher? Current => System.Windows.Application.Current?.Dispatcher;

    private static InvalidOperationException NoApplication() =>
        new("The application is shutting down, so it has no thread to run this on.");
}
