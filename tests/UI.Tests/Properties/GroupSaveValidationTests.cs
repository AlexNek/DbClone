using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using FsCheck.Xunit;

using NSubstitute;

using UI.Tests.Fakes;
using UI.Tests.Generators;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager, Property 17: Group save validation
/// Validates: Requirements 6.7
/// </summary>
public class GroupSaveValidationTests
{
    [Property(MaxTest = 100)]
    public bool Both_null_produces_validation_error()
    {
        // Arrange
        var connectionStore = new InMemoryConnectionStore();
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

        // Act — both null
        vm.NewGroupCommand.Execute(null);
        vm.GroupFormSourceConnection = null;
        vm.GroupFormDestinationConnection = null;

        vm.SaveGroupCommand.Execute(null);

        // Assert
        return !string.IsNullOrEmpty(vm.GroupValidationError) &&
               groupStore.GetAll().Count == 0;
    }

    /// <summary>
    /// For any group form state where GroupFormSourceConnection is null OR
    /// GroupFormDestinationConnection is null, invoking SaveGroup() SHALL set
    /// GroupValidationError to a non-null/non-empty string and SHALL NOT add
    /// or modify any entry in IConnectionGroupStore.
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool Source_null_destination_set_produces_validation_error(SavedConnection destination)
    {
        // Arrange
        var connectionStore = new InMemoryConnectionStore();
        var groupStore = new InMemoryConnectionGroupStore();
        var connectionStringService = Substitute.For<IConnectionStringService>();
        var maintenanceProvider = Substitute.For<IDatabaseMaintenanceProvider>();

        destination.Id = Guid.NewGuid().ToString("N");
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

        // Act — source null, destination set
        vm.NewGroupCommand.Execute(null);
        vm.GroupFormSourceConnection = null;
        vm.GroupFormDestinationConnection =
            vm.AvailableConnections.First(c => c.Id == destination.Id);

        vm.SaveGroupCommand.Execute(null);

        // Assert
        return !string.IsNullOrEmpty(vm.GroupValidationError) &&
               groupStore.GetAll().Count == 0;
    }

    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool Source_set_destination_null_produces_validation_error(SavedConnection source)
    {
        // Arrange
        var connectionStore = new InMemoryConnectionStore();
        var groupStore = new InMemoryConnectionGroupStore();
        var connectionStringService = Substitute.For<IConnectionStringService>();
        var maintenanceProvider = Substitute.For<IDatabaseMaintenanceProvider>();

        source.Id = Guid.NewGuid().ToString("N");
        connectionStore.Save(source);

        var vm = new UnifiedConnectionManagerViewModel(
            connectionStore,
            groupStore,
            connectionStringService,
            maintenanceProvider,
            Substitute.For<IConnectionImportService>(),
            Substitute.For<IConnectionExportService>(),
            Substitute.For<IBackupEncryptionService>(),
                        TestPlatformResolver.Create());

        // Act — source set, destination null
        vm.NewGroupCommand.Execute(null);
        vm.GroupFormSourceConnection = vm.AvailableConnections.First(c => c.Id == source.Id);
        vm.GroupFormDestinationConnection = null;

        vm.SaveGroupCommand.Execute(null);

        // Assert
        return !string.IsNullOrEmpty(vm.GroupValidationError) &&
               groupStore.GetAll().Count == 0;
    }
}
