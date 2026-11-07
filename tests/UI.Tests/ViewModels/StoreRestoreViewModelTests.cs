using DbClone.Application.Interfaces;
using DbClone.Application.Interchange;
using DbClone.UI.Models;
using DbClone.UI.Services;
using DbClone.UI.Settings;
using DbClone.UI.ViewModels;
using DbClone.UI.ViewModels.Workspaces.StoreRestore;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;

using UI.Tests.Fakes;

namespace UI.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="StoreRestoreViewModel"/> — workspace state machine,
/// direction switch, IWorkflowViewModel conformance, and the hand-off to
/// <see cref="IInterchangeService"/> (the product itself is always substituted,
/// so no database connection is opened here).
/// </summary>
public class StoreRestoreViewModelTests : IDisposable
{
    private readonly IInterchangeService _interchangeService;
    private readonly IDialogService _dialogService;
    private readonly ITableSelectionService _tableSelectionService;
    private readonly SettingsPersistenceManager _settingsPersister;
    private readonly UserSettings _settings;
    private readonly ConnectionViewModel _source;
    private readonly ConnectionViewModel _destination;
    private readonly OperationContext _ctx;
    private readonly StoreRestoreViewModel _sut;
    private readonly string _baseDirectory;

    public StoreRestoreViewModelTests()
    {
        _baseDirectory = Path.Combine(Path.GetTempPath(), "DbCloneTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDirectory);

        _interchangeService = Substitute.For<IInterchangeService>();
        _dialogService = Substitute.For<IDialogService>();
        _tableSelectionService = Substitute.For<ITableSelectionService>();
        _settings = new UserSettings();
        var settingsService = Substitute.For<ISettingsService>();
        _settingsPersister = new SettingsPersistenceManager(settingsService, _settings);
        _settingsPersister.Suspend(); // prevent file I/O during tests
        var logger = Substitute.For<ILogger<StoreRestoreViewModel>>();

        var connectionStringService = Substitute.For<IConnectionTextService>();
        var serverAdmin = Substitute.For<IServerAdminService>();
        _source = new ConnectionViewModel(connectionStringService, serverAdmin, TestPlatformResolver.Create())
        {
            Label = "Source",
            Host = "test.example.com",
            DatabaseName = "testdb"
        };
        _destination = new ConnectionViewModel(connectionStringService, serverAdmin, TestPlatformResolver.Create())
        {
            Label = "Destination",
            Host = "test.example.com",
            DatabaseName = "testdb"
        };
        _ctx = new OperationContext(_source, _destination);

        _sut = new StoreRestoreViewModel(
            _interchangeService,
            _dialogService,
            _tableSelectionService,
            _settingsPersister,
            _settings,
            _ctx,
            logger);
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
            Directory.Delete(_baseDirectory, recursive: true);
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

    [Fact]
    public async Task Store_delegates_to_interchange_service_with_selected_directory()
    {
        // Arrange
        _dialogService.BrowseFolder(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(_baseDirectory);
        _interchangeService
            .StoreAsync(Arg.Any<InterchangeStoreRequest>(), Arg.Any<IProgress<InterchangeProgressView>>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulStoreResult());

        // Act
        await _sut.StartStoreCommand.ExecuteAsync(null);

        // Assert
        await _interchangeService.Received(1).StoreAsync(
            Arg.Is<InterchangeStoreRequest>(r =>
                r.CatalogPath.StartsWith(_baseDirectory, StringComparison.Ordinal)
                && r.CatalogPath.Contains("testdb", StringComparison.Ordinal)
                && r.Connection.DatabaseName == "testdb"
                && r.TableSelection != null),
            Arg.Any<IProgress<InterchangeProgressView>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Store_is_blocked_when_source_has_no_database_name()
    {
        // Arrange
        _source.DatabaseName = string.Empty;

        // Act
        await _sut.StartStoreCommand.ExecuteAsync(null);

        // Assert — nothing requested, no directory dialog shown
        await _interchangeService.DidNotReceive()
            .StoreAsync(Arg.Any<InterchangeStoreRequest>(), Arg.Any<IProgress<InterchangeProgressView>>(), Arg.Any<CancellationToken>());
        _dialogService.DidNotReceive().BrowseFolder(Arg.Any<string>(), Arg.Any<string?>());
        _sut.State.BannerTitle.Should().Be("Store blocked");
    }

    [Fact]
    public async Task Store_reports_catalog_path_from_result()
    {
        // Arrange
        var catalogPath = Path.Combine(_baseDirectory, "testdb_catalog");
        _dialogService.BrowseFolder(Arg.Any<string>(), Arg.Any<string?>()).Returns(_baseDirectory);
        _interchangeService
            .StoreAsync(Arg.Any<InterchangeStoreRequest>(), Arg.Any<IProgress<InterchangeProgressView>>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulStoreResult() with { CatalogPath = catalogPath });

        // Act
        await _sut.StartStoreCommand.ExecuteAsync(null);

        // Assert
        _sut.State.StatusBarSummary.Should().Contain("Store succeeded");
        _sut.State.BannerMessage.Should().Contain(catalogPath);
    }

    [Fact]
    public async Task Restore_delegates_plan_and_import_to_interchange_service()
    {
        // Arrange
        var catalogPath = Path.Combine(_baseDirectory, "testdb_catalog");
        Directory.CreateDirectory(catalogPath);
        _dialogService.BrowseFolder(Arg.Any<string>(), Arg.Any<string?>()).Returns(catalogPath);
        _interchangeService
            .PlanRestoreAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new InterchangePlanView(6, 42, 0, [], []));
        _interchangeService
            .RestoreAsync(Arg.Any<InterchangeRestoreRequest>(), Arg.Any<IProgress<InterchangeProgressView>>(), Arg.Any<CancellationToken>())
            .Returns(new InterchangeRestoreResult(ERestoreOutcome.Success, 42, 0, 128L, 0, [], null, null, TimeSpan.FromSeconds(2)));

        // Act
        await _sut.StartRestoreCommand.ExecuteAsync(null);

        // Assert
        await _interchangeService.Received(1).PlanRestoreAsync(catalogPath, Arg.Any<CancellationToken>());
        await _interchangeService.Received(1).RestoreAsync(
            Arg.Is<InterchangeRestoreRequest>(r =>
                r.CatalogPath == catalogPath && r.Connection.DatabaseName == "testdb"),
            Arg.Any<IProgress<InterchangeProgressView>>(),
            Arg.Any<CancellationToken>());
        _sut.State.StatusBarSummary.Should().Contain("Restore succeeded");
        _sut.State.StatusBarSummary.Should().Contain("42 object(s)");
        _sut.State.StatusBarSummary.Should().Contain("128 row(s)");
    }

    [Fact]
    public async Task Restore_is_blocked_when_destination_has_no_database_name()
    {
        // Arrange
        _destination.DatabaseName = string.Empty;

        // Act
        await _sut.StartRestoreCommand.ExecuteAsync(null);

        // Assert
        await _interchangeService.DidNotReceive()
            .RestoreAsync(Arg.Any<InterchangeRestoreRequest>(), Arg.Any<IProgress<InterchangeProgressView>>(), Arg.Any<CancellationToken>());
        _sut.State.BannerTitle.Should().Be("Restore blocked");
    }

    [Fact]
    public async Task Store_leaves_not_running_and_busy_after_completion()
    {
        // Arrange
        _dialogService.BrowseFolder(Arg.Any<string>(), Arg.Any<string?>()).Returns(_baseDirectory);
        _interchangeService
            .StoreAsync(Arg.Any<InterchangeStoreRequest>(), Arg.Any<IProgress<InterchangeProgressView>>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulStoreResult());

        // Act
        await _sut.StartStoreCommand.ExecuteAsync(null);

        // Assert
        _sut.IsRunning.Should().BeFalse();
        _ctx.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Store_cancels_gracefully_when_service_throws_cancellation()
    {
        // Arrange
        _dialogService.BrowseFolder(Arg.Any<string>(), Arg.Any<string?>()).Returns(_baseDirectory);
        _interchangeService
            .StoreAsync(Arg.Any<InterchangeStoreRequest>(), Arg.Any<IProgress<InterchangeProgressView>>(), Arg.Any<CancellationToken>())
            .Returns<Task<InterchangeStoreResult>>(_ => throw new OperationCanceledException());

        // Act
        await _sut.StartStoreCommand.ExecuteAsync(null);

        // Assert
        _sut.State.StatusBarSummary.Should().Be("Store cancelled");
        _sut.IsRunning.Should().BeFalse();
    }

    private static InterchangeStoreResult SuccessfulStoreResult() =>
        new(
            EStoreOutcome.Success,
            SectionsWritten: 7,
            TablesExported: 3,
            RowsExported: 120,
            BytesExported: 4096,
            ChunksWritten: 1,
            IsComplete: true,
            ConsistencyModeUsed: "consistent",
            TablesThatFellBackToSourceOrder: [],
            VerificationPassed: true,
            Issues: [],
            CatalogPath: null,
            PackedZipPath: null,
            Elapsed: TimeSpan.FromSeconds(1));
}
