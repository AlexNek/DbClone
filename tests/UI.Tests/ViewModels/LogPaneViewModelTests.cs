using DbClone.Application.Enums;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using FluentAssertions;

namespace UI.Tests.ViewModels;

/// <summary>
/// Unit tests for LogPaneViewModel — mode-aware log display and commands.
/// </summary>
public class LogPaneViewModelTests
{
    [Fact]
    public void ClearLog_clears_only_active_workflow()
    {
        var (vm, copy, compare) = CreateViewModel();
        copy.Log("Message 1");
        copy.Log("Message 2");
        compare.Log("Compare message");

        vm.ClearLogCommand.Execute(null);

        vm.LogMessages.Should().BeEmpty();
        copy.LogMessages.Should().BeEmpty();
        compare.LogMessages.Should().ContainSingle();
    }

    [Fact]
    public void Defaults_to_copy_mode()
    {
        var (vm, copy, _) = CreateViewModel();

        vm.ActiveState.Should().BeSameAs(copy);
        vm.LogMessages.Should().BeSameAs(copy.LogMessages);
    }

    [Fact]
    public void IsErrorsOnly_false_shows_all_entries()
    {
        var (vm, copy, _) = CreateViewModel();
        copy.Log("All good");
        copy.LogWarning("Minor issue");
        copy.LogError("Stage failed");

        vm.IsErrorsOnly = true;
        vm.IsErrorsOnly = false;

        vm.FilteredLogMessages.Cast<LogEntry>().Should().HaveCount(3);
    }

    [Fact]
    public void IsErrorsOnly_filters_view_to_error_entries()
    {
        var (vm, copy, _) = CreateViewModel();
        copy.Log("All good");
        copy.LogWarning("Minor issue");
        copy.LogError("Stage failed");

        vm.IsErrorsOnly = true;

        vm.FilteredLogMessages.Cast<LogEntry>().Should().ContainSingle()
            .Which.Level.Should().Be(ELogLevel.Error);
    }

    [Fact]
    public void IsExpanded_follows_mode_switch()
    {
        var (vm, copy, compare) = CreateViewModel();
        copy.IsLogPaneExpanded = true;
        compare.IsLogPaneExpanded = false;

        vm.SetActiveMode(EWorkspaceMode.Compare);
        vm.IsExpanded.Should().BeFalse();

        vm.SetActiveMode(EWorkspaceMode.Copy);
        vm.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void IsExpanded_mirrors_active_state_IsLogPaneExpanded()
    {
        var (vm, copy, _) = CreateViewModel();

        vm.IsExpanded.Should().BeFalse();

        vm.IsExpanded = true;
        copy.IsLogPaneExpanded.Should().BeTrue();

        copy.IsLogPaneExpanded = false;
        vm.IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void IsExpanded_raises_PropertyChanged_when_active_state_changes()
    {
        var (vm, copy, compare) = CreateViewModel();
        var raised = 0;
        vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LogPaneViewModel.IsExpanded))
                    raised++;
            };

        copy.IsLogPaneExpanded = true;
        compare.IsLogPaneExpanded = true; // inactive workflow — must not raise

        raised.Should().Be(1);
    }

    [Fact]
    public void IsErrorsOnly_raises_PropertyChanged_when_active_state_changes()
    {
        var (vm, copy, compare) = CreateViewModel();
        var raised = 0;
        vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LogPaneViewModel.IsErrorsOnly))
                    raised++;
            };

        copy.IsErrorsOnly = true;
        compare.IsErrorsOnly = true; // inactive workflow — must not raise

        raised.Should().Be(1);
    }

    [Fact]
    public void LogMessages_reflects_active_state_messages()
    {
        var (vm, copy, _) = CreateViewModel();

        copy.Log("Message 1");
        copy.Log("Message 2");

        vm.LogMessages.Should().HaveCount(2);
        vm.LogMessages.Should().BeSameAs(copy.LogMessages);
    }

    [Fact]
    public void LogToggleSymbol_reflects_expansion_state()
    {
        var (vm, _, _) = CreateViewModel();

        vm.LogToggleSymbol.Should().Be("▶"); // collapsed

        vm.IsExpanded = true;
        vm.LogToggleSymbol.Should().Be("▼"); // expanded
    }

    [Fact]
    public void SelectedLogEntry_can_be_set()
    {
        var (vm, copy, _) = CreateViewModel();
        copy.Log("Test message");

        vm.SelectedLogEntry = vm.LogMessages[0];

        vm.SelectedLogEntry.Should().Be(vm.LogMessages[0]);
    }

    [Fact]
    public void SetActiveMode_errors_only_is_per_workflow()
    {
        var (vm, copy, compare) = CreateViewModel();
        copy.Log("info");
        copy.LogError("copy boom");
        compare.Log("compare info");
        compare.LogError("compare boom");

        // Enable filter in Copy mode
        vm.IsErrorsOnly = true;
        vm.FilteredLogMessages.Cast<LogEntry>().Should().ContainSingle();

        // Switch to Compare — filter must be independent (off by default)
        vm.SetActiveMode(EWorkspaceMode.Compare);
        vm.IsErrorsOnly.Should().BeFalse();
        vm.FilteredLogMessages.Cast<LogEntry>().Should().HaveCount(2);

        // Enable filter in Compare mode
        vm.IsErrorsOnly = true;
        vm.FilteredLogMessages.Cast<LogEntry>().Should().ContainSingle();

        // Switch back to Copy — its own filter is still on
        vm.SetActiveMode(EWorkspaceMode.Copy);
        vm.IsErrorsOnly.Should().BeTrue();
        vm.FilteredLogMessages.Cast<LogEntry>().Should().ContainSingle();
    }

    [Fact]
    public void SetActiveMode_switches_visible_collection()
    {
        var (vm, copy, compare) = CreateViewModel();
        copy.Log("Copy entry");
        compare.Log("Compare entry 1");
        compare.Log("Compare entry 2");

        vm.SetActiveMode(EWorkspaceMode.Compare);

        vm.ActiveState.Should().BeSameAs(compare);
        vm.LogMessages.Should().BeSameAs(compare.LogMessages);
        vm.FilteredLogMessages.Cast<LogEntry>().Should().HaveCount(2);

        vm.SetActiveMode(EWorkspaceMode.Copy);

        vm.ActiveState.Should().BeSameAs(copy);
        vm.FilteredLogMessages.Cast<LogEntry>().Should().ContainSingle();
    }

    [Fact]
    public void ToggleLogPane_toggles_active_state_expansion()
    {
        var (vm, copy, compare) = CreateViewModel();

        vm.ToggleLogPaneCommand.Execute(null);
        copy.IsLogPaneExpanded.Should().BeTrue();
        compare.IsLogPaneExpanded.Should().BeFalse();

        vm.ToggleLogPaneCommand.Execute(null);
        copy.IsLogPaneExpanded.Should().BeFalse();
    }

    private static (LogPaneViewModel vm, WorkflowState copyState, WorkflowState compareState)
        CreateViewModel()
    {
        var copyState = new WorkflowState();
        var compareState = new WorkflowState();
        var vm = new LogPaneViewModel(copyState, compareState);
        return (vm, copyState, compareState);
    }
}
