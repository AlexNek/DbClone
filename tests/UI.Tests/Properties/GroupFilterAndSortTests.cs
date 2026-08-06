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
/// Feature: unified-connection-manager, Property 2: Group filter and sort
/// Validates: Requirements 5.1, 10.5, 10.6
/// </summary>
public class GroupFilterAndSortTests
{
    /// <summary>
    /// For any set of ConnectionGroups and any search text (including empty string),
    /// FilteredGroups SHALL contain exactly those groups whose Name contains
    /// the search text using case-insensitive substring matching, and the results SHALL
    /// be sorted alphabetically by name using case-insensitive comparison.
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public Property FilteredGroups_contains_matching_sorted_by_name(
        ConnectionGroup[] groups,
        string searchText)
    {
        // Arrange
        var connectionStore = new InMemoryConnectionStore();
        var groupStore = new InMemoryConnectionGroupStore();
        foreach (var group in groups)
            groupStore.Save(group);

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
        vm.GroupSearchText = searchText ?? string.Empty;

        // Assert — compute expected result
        var effectiveSearch = searchText ?? string.Empty;
        var expected = groups
            .Where(g => g.Name.Contains(effectiveSearch, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var actual = vm.FilteredGroups.ToList();

        var result = actual.Count == expected.Count &&
                     actual.Select(g => g.Name).SequenceEqual(expected.Select(g => g.Name));

        return result.ToProperty();
    }
}
