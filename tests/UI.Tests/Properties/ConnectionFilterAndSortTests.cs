using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using FsCheck.Xunit;

using NSubstitute;

using UI.Tests.Fakes;
using UI.Tests.Generators;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager, Property 1: Connection filter and sort
/// Validates: Requirements 2.1, 10.2, 10.3
/// </summary>
public class ConnectionFilterAndSortTests
{
    /// <summary>
    /// For any set of SavedConnections and any search text (including empty string),
    /// FilteredConnections SHALL contain exactly those connections whose Name contains
    /// the search text using case-insensitive substring matching, and the results SHALL
    /// be sorted alphabetically by name using case-insensitive comparison.
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool FilteredConnections_contains_matching_sorted_by_name(
        SavedConnection[] connections,
        string searchText)
    {
        // Arrange
        var connectionStore = new InMemoryConnectionStore();
        foreach (var conn in connections)
            connectionStore.Save(conn);

        var groupStore = new InMemoryConnectionGroupStore();
        var connectionStringService = Substitute.For<IConnectionStringService>();
        var maintenanceProvider = Substitute.For<IDatabaseMaintenanceProvider>();

        var vm = new UnifiedConnectionManagerViewModel(
            connectionStore,
            groupStore,
            connectionStringService,
            maintenanceProvider,
            Substitute.For<IConnectionImportService>(),
            Substitute.For<IConnectionExportService>(),
            Substitute.For<IBackupEncryptionService>(),
                        TestPlatformResolver.Create());

        // Act — treat null search text as empty string
        vm.ConnectionSearchText = searchText ?? string.Empty;

        // Assert — compute expected result
        var effectiveSearch = searchText ?? string.Empty;
        var expected = connections
            .Where(c => c.Name.Contains(effectiveSearch, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var actual = vm.FilteredConnections.ToList();

        return actual.Count == expected.Count &&
               actual.Select(c => c.Name).SequenceEqual(expected.Select(c => c.Name));
    }
}
