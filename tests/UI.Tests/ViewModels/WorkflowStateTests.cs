using DbClone.Application.Enums;
using DbClone.UI.ViewModels;

using FluentAssertions;

using Wpf.Ui.Controls;

namespace UI.Tests.ViewModels;

/// <summary>
/// Unit tests for WorkflowState — per-workflow UI state
/// (logs, banner, status, objects panel).
/// </summary>
public class WorkflowStateTests
{
    [Fact]
    public void BeginNewRun_does_not_touch_other_workflow_state()
    {
        var copyState = new WorkflowState();
        var compareState = new WorkflowState();
        compareState.Log("compare entry");
        compareState.StatusBarSummary = "Compare done";
        compareState.LastError = "compare error";

        copyState.BeginNewRun();

        compareState.LogMessages.Should().ContainSingle();
        compareState.StatusBarSummary.Should().Be("Compare done");
        compareState.LastError.Should().Be("compare error");
        compareState.HasRun.Should().BeFalse();
    }

    [Fact]
    public void BeginNewRun_keeps_log_pane_expansion()
    {
        var state = new WorkflowState { IsLogPaneExpanded = true };

        state.BeginNewRun();

        state.IsLogPaneExpanded.Should().BeTrue("pane layout is per-workflow layout state, not run state");
    }

    [Fact]
    public void BeginNewRun_resets_transient_state_and_sets_HasRun()
    {
        var state = new WorkflowState();
        state.Log("old entry");
        state.LastError = "old error";
        state.StatusMessage = "Working...";
        state.StatusBarSummary = "Done";
        state.ElapsedTime = "12:34";

        state.BeginNewRun();

        state.HasRun.Should().BeTrue();
        state.LogMessages.Should().BeEmpty();
        state.LastError.Should().BeEmpty();
        state.IsBannerOpen.Should().BeFalse();
        state.StatusMessage.Should().BeEmpty();
        state.StatusBarSummary.Should().BeEmpty();
        state.ElapsedTime.Should().Be("00:00");
    }

    [Fact]
    public void Clearing_LastError_closes_banner()
    {
        var state = new WorkflowState();
        state.LastError = "Connection lost";

        state.LastError = string.Empty;

        state.IsBannerOpen.Should().BeFalse();
    }

    [Fact]
    public void Initial_state_has_sensible_defaults()
    {
        var state = new WorkflowState();

        state.HasRun.Should().BeFalse();
        state.ElapsedTime.Should().Be("00:00");
        state.StatusMessage.Should().Be("Ready");
        state.StatusBarSummary.Should().Be("Ready");
        state.LastError.Should().BeEmpty();
        state.IsBannerOpen.Should().BeFalse();
        state.IsLogPaneExpanded.Should().BeFalse();
        state.LogMessages.Should().BeEmpty();
        state.ObjectsPanel.Should().NotBeNull();
    }

    [Fact]
    public void LastError_mirrors_into_banner()
    {
        var state = new WorkflowState();

        state.LastError = "Connection lost";

        state.IsBannerOpen.Should().BeTrue();
        state.BannerTitle.Should().Be("Operation Failed");
        state.BannerMessage.Should().Be("Connection lost");
        state.BannerSeverity.Should().Be(InfoBarSeverity.Error);
    }

    [Fact]
    public void Log_adds_timestamped_info_entry()
    {
        var state = new WorkflowState();

        state.Log("Test message");

        state.LogMessages.Should().ContainSingle();
        var entry = state.LogMessages[0];
        entry.Level.Should().Be(ELogLevel.Info);
        entry.Display.Should().MatchRegex(@"^\[\d{2}:\d{2}:\d{2}\] Test message$");
    }

    [Fact]
    public void LogDetail_adds_untimestamped_indented_entry()
    {
        var state = new WorkflowState();

        state.LogDetail("Tables: 5");

        var entry = state.LogMessages.Should().ContainSingle().Which;
        entry.Timestamp.Should().BeNull();
        entry.Message.Should().Be("        Tables: 5");
    }

    [Fact]
    public void LogDetail_supports_explicit_level()
    {
        var state = new WorkflowState();

        state.LogDetail("Failed step", ELogLevel.Error);

        state.LogMessages[0].Level.Should().Be(ELogLevel.Error);
    }

    [Fact]
    public void LogError_adds_timestamped_error_entry()
    {
        var state = new WorkflowState();

        state.LogError("Boom");

        state.LogMessages[0].Level.Should().Be(ELogLevel.Error);
        state.LogMessages[0].Timestamp.Should().NotBeNull();
    }

    [Fact]
    public void LogHint_adds_hint_entry()
    {
        var state = new WorkflowState();

        state.LogHint("Try this");

        state.LogMessages[0].Level.Should().Be(ELogLevel.Hint);
    }

    [Fact]
    public void LogWarning_adds_warning_entry()
    {
        var state = new WorkflowState();

        state.LogWarning("Careful");

        state.LogMessages[0].Level.Should().Be(ELogLevel.Warning);
    }

    [Fact]
    public void ShowBanner_sets_all_banner_properties()
    {
        var state = new WorkflowState();

        state.ShowBanner("Title", "Body", InfoBarSeverity.Success);

        state.IsBannerOpen.Should().BeTrue();
        state.BannerTitle.Should().Be("Title");
        state.BannerMessage.Should().Be("Body");
        state.BannerSeverity.Should().Be(InfoBarSeverity.Success);
    }
}
