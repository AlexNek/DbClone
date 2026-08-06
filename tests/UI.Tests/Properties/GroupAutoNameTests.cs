using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using FsCheck.Xunit;

using NSubstitute;

using UI.Tests.Fakes;
using UI.Tests.Generators;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager, Property 16: Empty group name auto-generation
/// Validates: Requirements 6.4
/// </summary>
public class GroupAutoNameTests
{
    /// <summary>
    /// For any source connection with name S and destination connection with name D,
    /// when GroupFormName is empty or whitespace at the time SaveGroup() is invoked,
    /// the persisted group's Name SHALL equal "{S} → {D}".
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool Empty_group_name_generates_source_arrow_destination(
        SavedConnection source,
        SavedConnection destination)
    {
        // Arrange — seed store with two connections
        var connectionStore = new InMemoryConnectionStore();
        var groupStore = new InMemoryConnectionGroupStore();
        var connectionStringService = Substitute.For<IConnectionStringService>();
        var maintenanceProvider = Substitute.For<IDatabaseMaintenanceProvider>();

        // Ensure distinct IDs
        source.Id = Guid.NewGuid().ToString("N");
        destination.Id = Guid.NewGuid().ToString("N");

        connectionStore.Save(source);
        connectionStore.Save(destination);

        var vm = new UnifiedConnectionManagerViewModel(
            connectionStore,
            groupStore,
            connectionStringService,
            maintenanceProvider,
            Substitute.For<IConnectionImportService>(),
            Substitute.For<IConnectionExportService>(),
            Substitute.For<IBackupEncryptionService>(),
                        TestPlatformResolver.Create());

        // Act — start a new group form
        vm.NewGroupCommand.Execute(null);

        vm.GroupFormName = ""; // empty triggers auto-generation
        vm.GroupFormSourceConnection = vm.AvailableConnections.First(c => c.Id == source.Id);
        vm.GroupFormDestinationConnection =
            vm.AvailableConnections.First(c => c.Id == destination.Id);

        vm.SaveGroupCommand.Execute(null);

        // Assert — the persisted group's Name equals "{sourceName} → {destName}"
        var saved = groupStore.GetAll().FirstOrDefault();
        var expectedName = $"{source.Name} \u2192 {destination.Name}";

        return saved != null && saved.Name == expectedName;
    }
}
