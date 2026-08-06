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
/// Property 9: Connection delete removes from store
/// </summary>
public class ConnectionDeleteTests
{
    /// <summary>
    /// For any SavedConnection that exists in IConnectionStore, when it is selected
    /// and DeleteConnection() is invoked, IConnectionStore.GetById(id) SHALL return null.
    ///
    /// Validates: Requirements 3.4
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public Property Delete_connection_removes_from_store(SavedConnection connection)
    {
        Func<bool> property = () =>
            {
                // Arrange — seed store with the random connection
                var connectionStore = new InMemoryConnectionStore();
                connectionStore.Save(connection);

                var groupStore = new InMemoryConnectionGroupStore();
                var connectionStringService = Substitute.For<IConnectionStringService>();
                var maintenanceProvider = Substitute.For<IDatabaseMaintenanceProvider>();

                // Create VM — the connection will be auto-selected since it's the only one
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

                // Select the connection explicitly
                vm.SelectedConnection = connection;

                // Act — invoke DeleteConnectionCommand
                vm.DeleteConnectionCommand.Execute(null);

                // Assert — store no longer contains the connection
                return connectionStore.GetById(connection.Id) is null;
            };

        return property.ToProperty();
    }
}
