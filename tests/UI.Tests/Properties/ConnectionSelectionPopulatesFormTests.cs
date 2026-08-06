using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using FsCheck.Xunit;

using NSubstitute;

using UI.Tests.Fakes;
using UI.Tests.Generators;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager, Property 3: Connection selection populates form
/// Validates: Requirements 2.3
/// </summary>
public class ConnectionSelectionPopulatesFormTests
{
    /// <summary>
    /// For any SavedConnection, when it is assigned to SelectedConnection, all form fields
    /// (FormName, FormHost, FormPort, FormDatabaseName, FormUsername, FormPassword,
    /// FormSslMode, FormConnectionType, FormNotes, FormColor) SHALL equal the corresponding
    /// properties of that connection.
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool SelectedConnection_populates_all_form_fields(SavedConnection connection)
    {
        // Arrange — pre-seed the store with the connection
        var connectionStore = new InMemoryConnectionStore();
        connectionStore.Save(connection);

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

        // Act — select the connection
        vm.SelectedConnection = vm.FilteredConnections.First(c => c.Id == connection.Id);

        // Assert — all form fields match the connection's properties
        return vm.FormName == connection.Name &&
               vm.FormHost == connection.Host &&
               vm.FormPort == connection.Port &&
               vm.FormDatabaseName == connection.DatabaseName &&
               vm.FormUsername == connection.Username &&
               vm.FormPassword == connection.Password &&
               vm.FormSslMode == connection.SslMode &&
               vm.FormConnectionType == connection.ConnectionType &&
               vm.FormNotes == connection.Notes &&
               vm.FormColor == connection.Color;
    }
}
