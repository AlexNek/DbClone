using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using FsCheck.Xunit;

using NSubstitute;

using UI.Tests.Fakes;
using UI.Tests.Generators;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager, Property 14: Group save round-trip
/// Validates: Requirements 6.2
/// </summary>
public class GroupSaveRoundTripTests
{
    /// <summary>
    /// For any valid group form data (with both source and destination connections selected),
    /// invoking SaveGroup() SHALL result in IConnectionGroupStore containing a ConnectionGroup
    /// whose Name, SourceConnectionId, DestinationConnectionId, Notes, and Color match
    /// the form values.
    ///
    /// Validates: Requirements 6.2
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool Save_persists_group_with_form_values(
        ConnectionGroup template,
        SavedConnection sourceConn,
        SavedConnection destConn)
    {
        // Arrange — create VM with connections in the store
        var connectionStore = new InMemoryConnectionStore();
        connectionStore.Save(sourceConn);
        connectionStore.Save(destConn);

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
            TestPlatformResolver.Create(),
            initialTab: 1);

        // Act — reset form then populate with generated values
        vm.NewGroupCommand.Execute(null);

        vm.GroupFormName = template.Name;
        vm.GroupFormSourceConnection = vm.AvailableConnections.First(c => c.Id == sourceConn.Id);
        vm.GroupFormDestinationConnection = vm.AvailableConnections.First(c => c.Id == destConn.Id);
        vm.GroupFormNotes = template.Notes;
        vm.GroupFormColor = template.Color;

        vm.SaveGroupCommand.Execute(null);

        // Assert — store contains a group matching all form values (trimmed)
        var saved = groupStore.GetAll().FirstOrDefault();

        return saved != null &&
               saved.Name == template.Name.Trim() &&
               saved.SourceConnectionId == sourceConn.Id &&
               saved.DestinationConnectionId == destConn.Id &&
               saved.Notes == template.Notes.Trim() &&
               saved.Color == template.Color;
    }
}
