using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using FsCheck.Xunit;

using NSubstitute;

using UI.Tests.Fakes;
using UI.Tests.Generators;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager, Property 10: Empty connection name auto-generation
/// Validates: Requirements 3.6
/// </summary>
public class ConnectionAutoNameTests
{
    /// <summary>
    /// For any Host string and DatabaseName string, when FormName is empty or whitespace
    /// at the time SaveConnection() is invoked, the persisted connection's Name SHALL equal
    /// "{Host}/{DatabaseName}" (using the trimmed FormHost and FormDatabaseName values).
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool Empty_name_generates_host_slash_database(SavedConnection template)
    {
        // Arrange — create VM with empty stores
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

        // Act — reset form and set fields with empty name
        vm.NewConnectionCommand.Execute(null);

        vm.FormName = string.Empty; // Empty name triggers auto-generation
        vm.FormHost = template.Host;
        vm.FormPort = "5432"; // Use valid port to ensure save succeeds
        vm.FormDatabaseName = template.DatabaseName;
        vm.FormUsername = template.Username;
        vm.FormPassword = template.Password;
        vm.FormSslMode = "Prefer";
        vm.FormConnectionType = "postgresql";

        vm.SaveConnectionCommand.Execute(null);

        // Assert — the saved connection's Name equals "{host.Trim()}/{dbName.Trim()}"
        var saved = connectionStore.GetAll().FirstOrDefault();
        var expectedName = $"{template.Host.Trim()}/{template.DatabaseName.Trim()}";

        return saved != null && saved.Name == expectedName;
    }
}
