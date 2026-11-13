using CommunityToolkit.Mvvm.ComponentModel;

using DbClone.Application.Interchange;

namespace DbClone.UI.ViewModels;

/// <summary>
/// Presents a store/restore run on the shared progress card.
/// The card was written for the copy operation, so this answers the same properties from the
/// product's own interchange progress — which reports stages and objects, never rows.
/// </summary>
public sealed partial class StoreRestoreProgressViewModel : ObservableObject
{
    [ObservableProperty]
    private string _currentPhase = "Ready";

    [ObservableProperty]
    private int _progressPercent;

    // Bound to ProgressBar.Value, which is TwoWay by default: a read-only property here makes WPF
    // throw "A TwoWay or OneWayToSource binding cannot work on the read-only property" on attach.
    [ObservableProperty]
    private double _tableProgressPercent;

    public StoreRestoreProgressViewModel(WorkflowState state) =>
        State = state ?? throw new ArgumentNullException(nameof(state));

    /// <summary>
    /// Per-workflow state — the card reads <c>HasRun</c>, <c>ElapsedTime</c> and
    /// <c>StatusMessage</c> from it, so the run's log and banner stay one instance.
    /// </summary>
    public WorkflowState State { get; }

    /// <summary>A catalog operation moves objects, never rows, so the row-level block is not shown.</summary>
    public bool IsCopyingData => false;

    /// <summary>No speed and no estimate travel with a catalog operation.</summary>
    public bool IsDataTransferStatsVisible => false;

    // The row-level block and the speed/ETA block are hidden, and their bindings are answered here so
    // the card stays silent instead of reporting a binding failure nobody can act on.
    public string CurrentTable => "\u2014";

    public long RowsProcessed => 0;

    public long TotalRows => 0;

    public string TransferSpeed => "\u2014";

    public string EstimatedTimeRemaining => "\u2014";

    /// <summary>Returns the card to its pre-run shape before a new run reports anything.</summary>
    public void Reset()
    {
        ProgressPercent = 0;
        CurrentPhase = "Starting...";
        State.StatusMessage = string.Empty;
    }

    /// <summary>Takes one report from the product and moves the card to it.</summary>
    public void Report(InterchangeProgressView progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        ProgressPercent = Math.Clamp(progress.Percentage, 0, 100);
        CurrentPhase = progress.Stage;
        State.StatusMessage = Describe(progress);
    }

    /// <summary>The stage, and — while a section is walked — which object of how many.</summary>
    private static string Describe(InterchangeProgressView progress) =>
        progress.TableTotal > 0
            ? $"{progress.Stage} — {progress.TableOrdinal}/{progress.TableTotal} {progress.CurrentTable}".TrimEnd()
            : progress.Stage;
}
