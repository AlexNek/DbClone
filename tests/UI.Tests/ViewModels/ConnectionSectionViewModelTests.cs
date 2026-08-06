using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.Settings;
using DbClone.UI.ViewModels;

using FluentAssertions;

using NSubstitute;

using UI.Tests.Fakes;

namespace UI.Tests.ViewModels;

/// <summary>
/// Unit tests for ConnectionSectionViewModel — connection group management.
/// </summary>
public class ConnectionSectionViewModelTests
{
    [Fact]
    public void ClearGroupIfConnectionMismatch_clears_when_no_match()
    {
        var (vm, groupStore, ctx) = CreateViewModel();

        var conn1 = new SavedConnection { Id = "c1", Name = "Conn 1" };
        var conn2 = new SavedConnection { Id = "c2", Name = "Conn 2" };
        ctx.Source.RefreshSavedConnections([conn1, conn2]);
        ctx.Destination.RefreshSavedConnections([conn1, conn2]);

        groupStore.Save(
            new ConnectionGroup
                {
                    Id = "g1",
                    Name = "Group",
                    SourceConnectionId = "c1",
                    DestinationConnectionId = "c2"
                });

        // Select the group
        vm.SelectedConnectionGroup = vm.ConnectionGroups.First();

        // Change source connection to something that doesn't match
        ctx.Source.SelectedSavedConnection = conn2;

        vm.ClearGroupIfConnectionMismatch();

        vm.SelectedConnectionGroup.Should().BeNull();
    }

    [Fact]
    public void ClearGroupIfConnectionMismatch_keeps_matching_group()
    {
        var (vm, groupStore, ctx) = CreateViewModel();

        var conn1 = new SavedConnection { Id = "c1", Name = "Conn 1" };
        var conn2 = new SavedConnection { Id = "c2", Name = "Conn 2" };
        ctx.Source.RefreshSavedConnections([conn1, conn2]);
        ctx.Destination.RefreshSavedConnections([conn1, conn2]);

        groupStore.Save(
            new ConnectionGroup
                {
                    Id = "g1",
                    Name = "Group",
                    SourceConnectionId = "c1",
                    DestinationConnectionId = "c2"
                });

        // Set connections to match the group
        ctx.Source.SelectedSavedConnection = conn1;
        ctx.Destination.SelectedSavedConnection = conn2;

        vm.SelectedConnectionGroup = vm.ConnectionGroups.First();

        vm.ClearGroupIfConnectionMismatch();

        vm.SelectedConnectionGroup.Should().NotBeNull();
        vm.SelectedConnectionGroup!.Id.Should().Be("g1");
    }

    [Fact]
    public void ConnectionGroups_are_sorted_by_name()
    {
        var (vm, groupStore, _) = CreateViewModel();
        groupStore.Save(new ConnectionGroup { Id = "g1", Name = "Zebra" });
        groupStore.Save(new ConnectionGroup { Id = "g2", Name = "Apple" });
        groupStore.Save(new ConnectionGroup { Id = "g3", Name = "Mango" });

        vm.ConnectionGroups[0].Name.Should().Be("Apple");
        vm.ConnectionGroups[1].Name.Should().Be("Mango");
        vm.ConnectionGroups[2].Name.Should().Be("Zebra");
    }

    [Fact]
    public void Constructor_loads_existing_groups()
    {
        var (vm, groupStore, _) = CreateViewModel();
        groupStore.Save(new ConnectionGroup { Id = "g1", Name = "Group A" });
        groupStore.Save(new ConnectionGroup { Id = "g2", Name = "Group B" });

        // Trigger refresh by saving another group
        groupStore.Save(new ConnectionGroup { Id = "g3", Name = "Group C" });

        vm.ConnectionGroups.Should().HaveCount(3);
    }

    [Fact]
    public void RestoreLastUsedGroup_does_nothing_when_no_saved_id()
    {
        var settings = new UserSettings { SelectedConnectionGroupId = null };
        var (vm, groupStore, _) = CreateViewModel(settings);

        groupStore.Save(new ConnectionGroup { Id = "g1", Name = "Group A" });

        vm.RestoreLastUsedGroup();

        vm.SelectedConnectionGroup.Should().BeNull();
    }

    [Fact]
    public void RestoreLastUsedGroup_selects_saved_group()
    {
        var settings = new UserSettings { SelectedConnectionGroupId = "g2" };
        var (vm, groupStore, _) = CreateViewModel(settings);

        groupStore.Save(new ConnectionGroup { Id = "g1", Name = "Group A" });
        groupStore.Save(new ConnectionGroup { Id = "g2", Name = "Group B" });

        vm.RestoreLastUsedGroup();

        vm.SelectedConnectionGroup.Should().NotBeNull();
        vm.SelectedConnectionGroup!.Id.Should().Be("g2");
    }

    [Fact]
    public void Selecting_group_sets_source_and_destination_connections()
    {
        var (vm, groupStore, ctx) = CreateViewModel();

        var conn1 = new SavedConnection { Id = "c1", Name = "Source Conn" };
        var conn2 = new SavedConnection { Id = "c2", Name = "Dest Conn" };
        ctx.Source.RefreshSavedConnections([conn1, conn2]);
        ctx.Destination.RefreshSavedConnections([conn1, conn2]);

        groupStore.Save(
            new ConnectionGroup
                {
                    Id = "g1",
                    Name = "Group",
                    SourceConnectionId = "c1",
                    DestinationConnectionId = "c2"
                });

        vm.SelectedConnectionGroup = vm.ConnectionGroups.First();

        ctx.Source.SelectedSavedConnection?.Id.Should().Be("c1");
        ctx.Destination.SelectedSavedConnection?.Id.Should().Be("c2");
    }

    [Fact]
    public void Selecting_group_updates_settings()
    {
        var settings = new UserSettings();
        var (vm, groupStore, _) = CreateViewModel(settings);

        groupStore.Save(new ConnectionGroup { Id = "g1", Name = "Group A" });

        vm.SelectedConnectionGroup = vm.ConnectionGroups.First();

        settings.SelectedConnectionGroupId.Should().Be("g1");
    }

    private static (ConnectionSectionViewModel vm, InMemoryConnectionGroupStore groupStore,
        OperationContext ctx)
        CreateViewModel(UserSettings? settings = null)
    {
        var connectionStringService = Substitute.For<IConnectionStringService>();
        var maintenanceProvider = Substitute.For<IDatabaseMaintenanceProvider>();
        var connectionStore = new InMemoryConnectionStore();
        var groupStore = new InMemoryConnectionGroupStore();
        var settingsService = Substitute.For<ISettingsService>();
        var importService = Substitute.For<IConnectionImportService>();
        var exportService = Substitute.For<IConnectionExportService>();

        settings ??= new UserSettings();
        var settingsPersister = new SettingsPersistenceManager(settingsService, settings);
        settingsPersister.Suspend(); // Prevent auto-save during tests

        var source =
            new ConnectionViewModel(connectionStringService, maintenanceProvider, TestPlatformResolver.Create())
                {
                    Label = "Source"
                };
        var destination =
            new ConnectionViewModel(connectionStringService, maintenanceProvider, TestPlatformResolver.Create())
                {
                    Label = "Destination"
                };
        var ctx = new OperationContext(source, destination);

        var vm = new ConnectionSectionViewModel(
            groupStore,
            connectionStore,
            connectionStringService,
            maintenanceProvider,
            importService,
            exportService,
            settingsService,
            Substitute.For<IBackupEncryptionService>(),
            TestPlatformResolver.Create(),
            settingsPersister,
            settings,
            ctx);

        return (vm, groupStore, ctx);
    }
}
