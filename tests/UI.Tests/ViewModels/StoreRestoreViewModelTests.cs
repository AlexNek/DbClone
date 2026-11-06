using DbClone.Application.Interchange;
using DbClone.UI.Models;
using DbClone.UI.Services;
using DbClone.UI.Settings;
using DbClone.UI.ViewModels;
using DbClone.UI.ViewModels.Workspaces.StoreRestore;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;

namespace UI.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="StoreRestoreViewModel"/> — workspace state machine,
/// direction switch, and IWorkflowViewModel conformance.
/// </summary>
public class StoreRestoreViewModelTests
{
    private readonly IInterchangeService _interchangeService;
    private readonly SettingsPersistenceManager _settingsPersister;
    private readonly UserSettings _settings;
    private readonly StoreRestoreViewModel _sut;

    public StoreRestoreViewModelTests()
    {
        _interchangeService = Substitute.For<IInterchangeService>();
        _settings = new UserSettings();
        var settingsService = Substitute.For<ISettingsService>();
        _settingsPersister = new SettingsPersistenceManager(settingsService, _settings);
        _settingsPersister.Suspend(); // prevent file I/O during tests
        var logger = Substitute.For<ILogger<StoreRestoreViewModel>>();

        _sut = new StoreRestoreViewModel(_interchangeService, _settingsPersister, _settings, logger);
    }

    [Fact]
    public void Defaults_to_store_direction()
    {
        _sut.IsStoreDirection.Should().BeTrue();
    }

    [Fact]
    public void SwitchToRestore_sets_direction_to_false()
    {
        _sut.SwitchToRestoreCommand.Execute(null);
        _sut.IsStoreDirection.Should().BeFalse();
    }

    [Fact]
    public void SwitchToStore_sets_direction_to_true()
    {
        _sut.SwitchToRestoreCommand.Execute(null);
        _sut.SwitchToStoreCommand.Execute(null);
        _sut.IsStoreDirection.Should().BeTrue();
    }

    [Fact]
    public void IsRunning_initially_false()
    {
        ((IWorkflowViewModel)_sut).IsRunning.Should().BeFalse();
    }

    [Fact]
    public void State_is_not_null()
    {
        _sut.State.Should().NotBeNull();
    }

    [Fact]
    public void State_is_independent_instance()
    {
        var state1 = _sut.State;
        var state2 = _sut.State;
        state1.Should().BeSameAs(state2);
    }

    [Fact]
    public void PauseCommand_does_not_throw_when_not_running()
    {
        var act = () => _sut.PauseCommand.Execute(null);
        act.Should().NotThrow();
    }

    [Fact]
    public void StopCommand_does_not_throw_when_not_running()
    {
        var act = () => _sut.StopCommand.Execute(null);
        act.Should().NotThrow();
    }

    [Fact]
    public void EWorkspaceMode_has_two_values()
    {
        Enum.GetValues<EWorkspaceMode>().Should().HaveCount(2);
        Enum.GetNames<EWorkspaceMode>().Should().Contain("Copy");
        Enum.GetNames<EWorkspaceMode>().Should().Contain("Compare");
    }
}
