using System.Diagnostics;
using System.Windows.Threading;

namespace DbClone.UI.Services;

/// <summary>
/// Manages the UI state machine for copy/compare operations.
/// Handles atomic state transitions (Idle → Running → Complete/Failed/Cancelled)
/// and the repetitive setup/teardown of progress-related properties.
/// </summary>
public sealed class ViewModelStateManager
{
    /// <summary>Fired each second with the formatted elapsed time string.</summary>
    public event Action<string>? ElapsedTimeUpdated;

    private Stopwatch? _elapsedSw;

    private DispatcherTimer? _elapsedTimer;

    /// <summary>Current high-level state of the operation.</summary>
    public EOperationState State { get; private set; } = EOperationState.Idle;

    /// <summary>
    /// Transitions to the Running state: starts elapsed timer, resets progress tracking.
    /// </summary>
    public void BeginOperation()
    {
        State = EOperationState.Running;

        _elapsedSw = Stopwatch.StartNew();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += OnElapsedTick;
        _elapsedTimer.Start();
    }

    /// <summary>
    /// Transitions to a terminal state and stops the elapsed timer.
    /// </summary>
    public void EndOperation(EOperationState finalState)
    {
        State = finalState;
        StopTimer();
    }

    /// <summary>
    /// Gets the current elapsed time as a formatted string.
    /// </summary>
    public string GetElapsedFormatted()
    {
        if (_elapsedSw == null)
        {
            return "00:00";
        }

        return FormatElapsed(_elapsedSw.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Resets back to Idle (e.g. after the finally block completes).
    /// </summary>
    public void Reset()
    {
        State = EOperationState.Idle;
        StopTimer();
    }

    private static string FormatElapsed(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    private void OnElapsedTick(object? sender, EventArgs e)
    {
        if (_elapsedSw != null)
        {
            ElapsedTimeUpdated?.Invoke(FormatElapsed(_elapsedSw.Elapsed.TotalSeconds));
        }
    }

    private void StopTimer()
    {
        _elapsedTimer?.Stop();
        if (_elapsedTimer != null)
        {
            _elapsedTimer.Tick -= OnElapsedTick;
        }

        _elapsedTimer = null;
        _elapsedSw?.Stop();
        _elapsedSw = null;
    }
}
