using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using NSubstitute;

using UI.Tests.Fakes;
using UI.Tests.Generators;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager, Property 15: Group delete removes from store
/// Validates: Requirements 6.3
/// </summary>
public class GroupDeleteTests
{
    /// <summary>
    /// For any ConnectionGroup that exists in IConnectionGroupStore, when it is selected
    /// and DeleteGroup() is invoked, IConnectionGroupStore.GetById(id) SHALL return null.
    ///
    /// Validates: Requirements 6.3
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public Property Delete_group_removes_from_store(
        ConnectionGroup group,
        SavedConnection sourceConn,
        SavedConnection destConn)
    {
        Func<bool> property = () =>
            {
                // Arrange — seed connections and group store
                var connectionStore = new InMemoryConnectionStore();
                connectionStore.Save(sourceConn);
                connectionStore.Save(destConn);

                // Wire the group to reference actual connection IDs
                group.SourceConnectionId = sourceConn.Id;
                group.DestinationConnectionId = destConn.Id;

                var groupStore = new InMemoryConnectionGroupStore();
                groupStore.Save(group);

                var connectionStringService = Substitute.For<IConnectionStringService>();
                var maintenanceProvider = Substitute.For<IDatabaseMaintenanceProvider>();

                // Create VM on groups tab — the group will appear in FilteredGroups
                var vm = new UnifiedConnectionManagerViewModel(
                    connectionStore,
                    groupStore,
                    connectionStringService,
                    maintenanceProvider,
                    Substitute.For<IConnectionImportService>(),
                    Substitute.For<IConnectionExportService>(),
                    Substitute.For<IBackupEncryptionService>(),
                    TestPlatformResolver.Create(),
                    initialTab: 1);

                // Select the group
                vm.SelectedGroup = vm.FilteredGroups.First(g => g.Id == group.Id);

                // Act — invoke DeleteGroupCommand
                vm.DeleteGroupCommand.Execute(null);

                // Assert — store no longer contains the group
                return groupStore.GetById(group.Id) is null;
            };

        return property.ToProperty();
    }
}
