using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using FsCheck.Xunit;

using NSubstitute;

using UI.Tests.Fakes;
using UI.Tests.Generators;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager, Property 13: New group resets form to defaults
/// Validates: Requirements 6.1
/// </summary>
public class NewGroupResetsFormTests
{
    /// <summary>
    /// For any group form state, invoking NewGroup() SHALL reset:
    /// GroupFormName=empty, GroupFormSourceConnection=null, GroupFormDestinationConnection=null,
    /// GroupFormNotes=empty, GroupFormColor=null, and IsEditingGroup SHALL be false.
    ///
    /// Validates: Requirements 6.1
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool NewGroup_resets_form_to_defaults(
        ConnectionGroup group,
        SavedConnection sourceConn,
        SavedConnection destConn)
    {
        // Arrange — seed connections and group store with known data
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

        // Populate form with non-default values by selecting the group
        vm.SelectedGroup = vm.FilteredGroups.FirstOrDefault(g => g.Id == group.Id);

        // Act — invoke NewGroupCommand to reset the form
        vm.NewGroupCommand.Execute(null);

        // Assert — all group form fields are reset to defaults
        return vm.GroupFormName == "" &&
               vm.GroupFormSourceConnection == null &&
               vm.GroupFormDestinationConnection == null &&
               vm.GroupFormNotes == "" &&
               vm.GroupFormColor == null &&
               !vm.IsEditingGroup;
    }
}
