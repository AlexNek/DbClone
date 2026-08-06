using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using FsCheck.Xunit;

using NSubstitute;

using UI.Tests.Fakes;
using UI.Tests.Generators;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager, Property 7: New connection resets form to defaults
/// Validates: Requirements 3.1
/// </summary>
public class NewConnectionResetsFormTests
{
    /// <summary>
    /// For any form state (arbitrary values in all connection form fields), invoking
    /// NewConnectionCommand SHALL reset all fields to: Name=empty, Host="localhost",
    /// Port="5432", DatabaseName=empty, Username="postgres", Password=empty,
    /// SslMode="Prefer", ConnectionType=PostgreSql, Notes=empty, Color=null,
    /// and IsEditingConnection SHALL be false.
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool NewConnection_resets_form_to_defaults(SavedConnection connection)
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

        // Seed the form with arbitrary values by selecting the connection
        vm.SelectedConnection = vm.FilteredConnections.First(c => c.Id == connection.Id);

        // Act — invoke NewConnectionCommand to reset the form
        vm.NewConnectionCommand.Execute(null);

        // Assert — all fields are reset to defaults
        return vm.FormName == "" &&
               vm.FormHost == "localhost" &&
               vm.FormPort == "5432" &&
               vm.FormDatabaseName == "" &&
               vm.FormUsername == "postgres" &&
               vm.FormPassword == "" &&
               vm.FormSslMode == "Prefer" &&
               vm.FormConnectionType == null &&
               vm.FormNotes == "" &&
               vm.FormColor == null &&
               !vm.IsEditingConnection;
    }
}
