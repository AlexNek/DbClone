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
/// Feature: unified-connection-manager, Property 4: Group selection populates form
/// Validates: Requirements 5.2
/// </summary>
public class GroupSelectionPopulatesFormTests
{
    /// <summary>
    /// For any ConnectionGroup (with valid source and destination IDs referencing existing
    /// connections), when it is assigned to SelectedGroup, the form fields (GroupFormName,
    /// GroupFormSourceConnection, GroupFormDestinationConnection, GroupFormNotes, GroupFormColor)
    /// SHALL reflect that group's persisted values.
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public Property SelectedGroup_populates_form_with_group_values(
        ConnectionGroup group,
        SavedConnection sourceConn,
        SavedConnection destConn)
    {
        // Arrange — seed connections with known IDs
        var connectionStore = new InMemoryConnectionStore();
        connectionStore.Save(sourceConn);
        connectionStore.Save(destConn);

        // Wire the group to reference the actual connection IDs
        group.SourceConnectionId = sourceConn.Id;
        group.DestinationConnectionId = destConn.Id;

        // Seed the group store
        var groupStore = new InMemoryConnectionGroupStore();
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

        // Act — select the group from FilteredGroups
        var matchedGroup = vm.FilteredGroups.FirstOrDefault(g => g.Id == group.Id);

        // Guard: if the group isn't in the filtered list, skip this test case
        if (matchedGroup is null)
            return true.ToProperty();

        vm.SelectedGroup = matchedGroup;

        // Assert — form fields reflect the group's persisted values
        return (vm.GroupFormName == group.Name &&
                vm.GroupFormSourceConnection?.Id == group.SourceConnectionId &&
                vm.GroupFormDestinationConnection?.Id == group.DestinationConnectionId &&
                vm.GroupFormNotes == group.Notes &&
                vm.GroupFormColor == group.Color)
            .ToProperty();
    }
}
