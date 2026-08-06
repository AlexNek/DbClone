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
/// Feature: unified-connection-manager
/// Property 5: Auto-select first connection
/// Property 6: Auto-select first group
/// </summary>
public class AutoSelectFirstTests
{
    /// <summary>
    /// For any non-empty set of SavedConnections, when the ViewModel initializes,
    /// SelectedConnection SHALL equal the first connection in case-insensitive
    /// alphabetical order by name.
    ///
    /// Validates: Requirements 2.5
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public Property AutoSelect_first_connection_on_init(SavedConnection[] connections)
    {
        Func<bool> property = () =>
            {
                // Arrange
                var connectionStore = new InMemoryConnectionStore();
                foreach (var conn in connections)
                    connectionStore.Save(conn);

                var groupStore = new InMemoryConnectionGroupStore();
                var connectionStringService = Substitute.For<IConnectionStringService>();
                var maintenanceProvider = Substitute.For<IDatabaseMaintenanceProvider>();

                // Act
                var vm = new UnifiedConnectionManagerViewModel(
                    connectionStore,
                    groupStore,
                    connectionStringService,
                    maintenanceProvider,
                    Substitute.For<IConnectionImportService>(),
                    Substitute.For<IConnectionExportService>(),
                    Substitute.For<IBackupEncryptionService>(),
                    TestPlatformResolver.Create(),
                    initialTab: 0);

                // Assert — SelectedConnection should be the first alphabetically by name
                var expectedFirst = connections
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .First();

                return vm.SelectedConnection != null &&
                       vm.SelectedConnection.Name == expectedFirst.Name;
            };

        return property.When(connections.Length > 0);
    }

    /// <summary>
    /// For any non-empty set of ConnectionGroups, when the Groups tab activates
    /// (initialTab == 1), SelectedGroup SHALL equal the first group in
    /// case-insensitive alphabetical order by name.
    ///
    /// Validates: Requirements 5.5
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public Property AutoSelect_first_group_on_groups_tab(ConnectionGroup[] groups)
    {
        Func<bool> property = () =>
            {
                // Arrange
                var connectionStore = new InMemoryConnectionStore();
                var groupStore = new InMemoryConnectionGroupStore();
                foreach (var group in groups)
                    groupStore.Save(group);

                var connectionStringService = Substitute.For<IConnectionStringService>();
                var maintenanceProvider = Substitute.For<IDatabaseMaintenanceProvider>();

                // Act — initialTab: 1 activates the Groups tab
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

                // Assert — SelectedGroup should be the first alphabetically by name
                var expectedFirst = groups
                    .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    .First();

                return vm.SelectedGroup != null &&
                       vm.SelectedGroup.Name == expectedFirst.Name;
            };

        return property.When(groups.Length > 0);
    }
}
