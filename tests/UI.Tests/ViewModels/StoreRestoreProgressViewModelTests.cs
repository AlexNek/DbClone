using DbClone.Application.Interchange;
using DbClone.UI.ViewModels;

using FluentAssertions;

using Xunit;

namespace DbClone.UI.Tests.ViewModels;

/// <summary>
/// The progress card is one view for every workflow. A store/restore answers it from the
/// product's own reports, so these pin what the card states while a catalog operation runs.
/// </summary>
public sealed class StoreRestoreProgressViewModelTests
{
    [Fact]
    public void Report_moves_the_bar_to_the_percentage_the_product_stated()
    {
        // Arrange — the exporter's own "Writing tables" report, a fifth of the way in.
        var state = new WorkflowState();
        var sut = new StoreRestoreProgressViewModel(state);

        // Act
        sut.Report(Progress(stage: "Writing tables", percentage: 20));

        // Assert
        sut.ProgressPercent.Should().Be(20);
        sut.CurrentPhase.Should().Be("Writing tables");
    }

    [Fact]
    public void Report_names_the_object_a_walking_stage_reached()
    {
        // Arrange — through an already-run store, so the run's own log is the one reported into.
        var state = new WorkflowState();
        var sut = new StoreRestoreProgressViewModel(state);

        // Act
        sut.Report(Progress(stage: "Importing data", percentage: 70, processed: 3, total: 12, current: "public.orders"));

        // Assert — the card's bottom line says which object of how many, so a stage that takes
        // minutes is never a bar and no words of its own.
        state.StatusMessage.Should().Be("Importing data — 3/12 public.orders");
    }

    [Fact]
    public void Report_of_a_stage_that_walks_no_objects_states_only_the_stage()
    {
        var state = new WorkflowState();
        var sut = new StoreRestoreProgressViewModel(state);

        sut.Report(Progress(stage: "Verifying catalog", percentage: 5));

        state.StatusMessage.Should().Be("Verifying catalog");
    }

    [Fact]
    public void Report_keeps_the_bar_inside_its_own_range()
    {
        var state = new WorkflowState();
        var sut = new StoreRestoreProgressViewModel(state);

        sut.Report(Progress(stage: "Complete", percentage: 140));

        sut.ProgressPercent.Should().Be(100);
    }

    [Fact]
    public void The_row_level_block_is_not_shown_for_a_catalog_operation()
    {
        // A catalog moves objects and reports neither rows nor a speed, so the card shows the
        // overall bar, the stage, the elapsed time and the status line — nothing it cannot answer.
        var sut = new StoreRestoreProgressViewModel(new WorkflowState());

        sut.IsCopyingData.Should().BeFalse();
        sut.IsDataTransferStatsVisible.Should().BeFalse();
    }

    [Fact]
    public void Reset_returns_the_card_to_its_pre_run_shape()
    {
        var state = new WorkflowState();
        var sut = new StoreRestoreProgressViewModel(state);
        sut.Report(Progress(stage: "Writing data", percentage: 90, processed: 9, total: 10, current: "public.orders"));

        sut.Reset();

        sut.ProgressPercent.Should().Be(0);
        sut.CurrentPhase.Should().Be("Starting...");
        state.StatusMessage.Should().BeEmpty();
    }

    [Fact]
    public void The_card_reads_the_run_state_it_was_handed()
    {
        // The log, the banner and the elapsed time belong to the run's own state — one instance,
        // not a copy the card keeps beside it.
        var state = new WorkflowState();

        var sut = new StoreRestoreProgressViewModel(state);

        sut.State.Should().BeSameAs(state);
    }

    [Theory]
    [InlineData(nameof(StoreRestoreProgressViewModel.ProgressPercent))]
    [InlineData(nameof(StoreRestoreProgressViewModel.TableProgressPercent))]
    public void The_bars_answer_the_two_way_binding_the_card_puts_on_them(string propertyName)
    {
        // ProgressBar.Value binds TwoWay by default, and WPF throws on attach when the source is a
        // read-only property — the card adopted this view model and the whole app died with it.
        var property = typeof(StoreRestoreProgressViewModel).GetProperty(propertyName);

        property.Should().NotBeNull();
        property!.CanWrite.Should().BeTrue();
        property.SetMethod!.IsPublic.Should().BeTrue();
    }

    [Fact]
    public void Finish_ends_the_card_on_a_failure_and_keeps_the_reason_on_it()
    {
        // Arrange — a run that failed before its first stage: "Starting" is the only report it sent.
        var state = new WorkflowState();
        var sut = new StoreRestoreProgressViewModel(state);
        sut.Report(Progress(stage: "Starting", percentage: 0));

        // Act — the outcome the workflow states for the same run.
        sut.Finish("Failed", "Restore failed: the catalog cannot be read");

        // Assert — the card says how the run ended and why, instead of standing at "Starting" for good
        // while the reason lives only in a banner the reader can close.
        sut.CurrentPhase.Should().Be("Failed");
        state.StatusMessage.Should().Be("Restore failed: the catalog cannot be read");
    }

    [Fact]
    public void Finish_states_the_ending_it_is_handed_instead_of_collapsing_every_other_one_into_a_failure()
    {
        // Arrange — one card per ending a store or a restore can reach. A refusal is not a failure: the
        // target held objects, so the run stopped and nothing was wrong with it.
        var completedState = new WorkflowState();
        var completed = new StoreRestoreProgressViewModel(completedState);

        var cancelledState = new WorkflowState();
        var cancelled = new StoreRestoreProgressViewModel(cancelledState);

        var refusedState = new WorkflowState();
        var refused = new StoreRestoreProgressViewModel(refusedState);

        // Act
        completed.Finish("Complete", "Store succeeded");
        cancelled.Finish("Cancelled", "Restore cancelled after 3 objects");
        refused.Finish("Refused", "Restore refused — target database not empty");

        // Assert
        completed.CurrentPhase.Should().Be("Complete");
        completedState.StatusMessage.Should().Be("Store succeeded");
        cancelled.CurrentPhase.Should().Be("Cancelled");
        cancelledState.StatusMessage.Should().Be("Restore cancelled after 3 objects");
        refused.CurrentPhase.Should().Be("Refused");
        refusedState.StatusMessage.Should().Be("Restore refused — target database not empty");
    }

    private static InterchangeProgressView Progress(
        string stage,
        int percentage,
        int processed = 0,
        int total = 0,
        string current = "") =>
        new(
            Stage: stage,
            Percentage: percentage,
            TableOrdinal: processed,
            TableTotal: total,
            CurrentTable: current,
            RowsDone: 0,
            RowsTotal: 0,
            BytesDone: 0,
            BytesTotal: 0,
            Elapsed: TimeSpan.Zero,
            Remaining: null);
}
