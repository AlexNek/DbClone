using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using FsCheck.Xunit;

using NSubstitute;

using UI.Tests.Fakes;
using UI.Tests.Generators;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager
/// Property 18: Available connections reactivity
/// Validates: Requirements 7.1, 7.2, 7.3
/// </summary>
public class AvailableConnectionsReactivityTests
{
    /// <summary>
    /// For any sequence of add and delete operations, AvailableConnections SHALL always
    /// equal store.GetAll() sorted alphabetically by name (case-insensitive).
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool AvailableConnections_always_matches_store_sorted(SavedConnection[] connections)
    {
        // Arrange - create VM with empty store
        var connectionStore = new InMemoryConnectionStore();
        var vm = CreateVm(connectionStore);

        // Act - add all, then delete the first half
        foreach (var conn in connections)
            connectionStore.Save(conn);

        var toDelete = connections.Take(connections.Length / 2).ToArray();
        foreach (var conn in toDelete)
            connectionStore.Delete(conn.Id);

        // Assert - AvailableConnections matches store.GetAll() sorted by name
        var expected = connectionStore.GetAll()
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var actual = vm.AvailableConnections.ToList();

        return actual.Count == expected.Count &&
               actual.Select(c => c.Id).SequenceEqual(expected.Select(c => c.Id));
    }

    /// <summary>
    /// For any set of SavedConnections added to the store after VM creation,
    /// AvailableConnections SHALL update to match store.GetAll() sorted alphabetically by name.
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool AvailableConnections_updates_when_connections_added(SavedConnection[] connections)
    {
        // Arrange - create VM with empty store
        var connectionStore = new InMemoryConnectionStore();
        var vm = CreateVm(connectionStore);

        // Act - add connections one by one after VM creation
        foreach (var conn in connections)
            connectionStore.Save(conn);

        // Assert - AvailableConnections matches store.GetAll() sorted by name
        var expected = connectionStore.GetAll()
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var actual = vm.AvailableConnections.ToList();

        return actual.Count == expected.Count &&
               actual.Select(c => c.Id).SequenceEqual(expected.Select(c => c.Id));
    }

    /// <summary>
    /// When a connection currently selected in GroupFormSourceConnection is deleted,
    /// that field SHALL become null.
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool Deleting_selected_source_connection_clears_dropdown(SavedConnection connection)
    {
        // Arrange - seed the store with the connection
        var connectionStore = new InMemoryConnectionStore();
        connectionStore.Save(connection);

        var vm = CreateVm(connectionStore);

        // Select the connection in the group form source dropdown
        vm.GroupFormSourceConnection = vm.AvailableConnections.First(c => c.Id == connection.Id);

        // Act - delete the connection
        connectionStore.Delete(connection.Id);

        // Assert - source dropdown is now null
        return vm.GroupFormSourceConnection == null;
    }

    private static UnifiedConnectionManagerViewModel CreateVm(
        InMemoryConnectionStore connectionStore,
        InMemoryConnectionGroupStore? groupStore = null)
    {
        return new UnifiedConnectionManagerViewModel(
            connectionStore,
            groupStore ?? new InMemoryConnectionGroupStore(),
            Substitute.For<IConnectionStringService>(),
            Substitute.For<IDatabaseMaintenanceProvider>(),
            Substitute.For<IConnectionImportService>(),
            Substitute.For<IConnectionExportService>(),
            Substitute.For<IBackupEncryptionService>(),
            TestPlatformResolver.Create());
    }
}
