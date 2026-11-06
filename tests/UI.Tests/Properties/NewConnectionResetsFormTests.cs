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
    /// NewConnectionCommand SHALL reset every field to blank, except the connection defaults
    /// (host, port, TLS, username, connection type), which come from the injected platform
    /// service — the UI writes no engine default of its own (refactors/09 P5).
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool NewConnection_resets_form_to_defaults(SavedConnection connection)
    {
        // Arrange — pre-seed the store with the connection
        var connectionStore = new InMemoryConnectionStore();
        connectionStore.Save(connection);

        var groupStore = new InMemoryConnectionGroupStore();
        var connectionStringService = Substitute.For<IConnectionTextService>();
        var maintenanceProvider = Substitute.For<IServerAdminService>();

        // The defaults the form must land on are whatever the active engine's platform stub reports;
        // with an empty platform list the reset resolves them through the null (plain-engine) id.
        var platformService = TestPlatformResolver.Create();
        var defaults = platformService.GetConnectionDefaults(null);

        var vm = new UnifiedConnectionManagerViewModel(
            connectionStore,
            groupStore,
            connectionStringService,
            maintenanceProvider,
            Substitute.For<IConnectionImportService>(),
            Substitute.For<IConnectionExportService>(),
            Substitute.For<IBackupEncryptionService>(),
            platformService);

        // Seed the form with arbitrary values by selecting the connection
        vm.SelectedConnection = vm.FilteredConnections.First(c => c.Id == connection.Id);

        // Act — invoke NewConnectionCommand to reset the form
        vm.NewConnectionCommand.Execute(null);

        // Assert — all fields are reset; the connection defaults match the platform stub
        return vm.FormName == "" &&
               vm.FormHost == "localhost" &&
               vm.FormPort == defaults.Port.ToString() &&
               vm.FormDatabaseName == "" &&
               vm.FormUsername == defaults.Username &&
               vm.FormPassword == "" &&
               vm.FormSslMode == defaults.SslMode &&
               vm.FormConnectionType == null &&
               vm.FormNotes == "" &&
               vm.FormColor == null &&
               !vm.IsEditingConnection;
    }
}
