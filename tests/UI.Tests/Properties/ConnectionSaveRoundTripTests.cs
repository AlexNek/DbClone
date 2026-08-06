using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using FsCheck.Xunit;

using NSubstitute;

using UI.Tests.Fakes;
using UI.Tests.Generators;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager, Property 8: Connection save round-trip
/// Validates: Requirements 3.2, 3.3
/// </summary>
public class ConnectionSaveRoundTripTests
{
    /// <summary>
    /// For any valid connection form data (non-empty Host, port in range 1–65535),
    /// invoking SaveConnection() SHALL result in IConnectionStore containing a
    /// SavedConnection whose properties match the form field values at the time of save.
    /// </summary>
    [Property(Arbitrary = [typeof(ArbitraryConnections)], MaxTest = 100)]
    public bool Save_persists_connection_with_form_values(SavedConnection template)
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

        // Act — reset form and populate with generated values
        vm.NewConnectionCommand.Execute(null);

        vm.FormName = template.Name;
        // Set ConnectionType FIRST — it triggers preset that overwrites Port/SslMode
        vm.FormConnectionType = template.ConnectionType;
        // Then override Port and SslMode after preset has fired
        vm.FormHost = template.Host;
        vm.FormPort = template.Port;
        vm.FormDatabaseName = template.DatabaseName;
        vm.FormUsername = template.Username;
        vm.FormPassword = template.Password;
        vm.FormSslMode = template.SslMode;
        vm.FormNotes = template.Notes;
        vm.FormColor = template.Color;

        vm.SaveConnectionCommand.Execute(null);

        // Assert — store contains a connection matching all form values (trimmed)
        var saved = connectionStore.GetAll().FirstOrDefault();

        return saved != null &&
               saved.Name == template.Name.Trim() &&
               saved.Host == template.Host.Trim() &&
               saved.Port == template.Port.Trim() &&
               saved.DatabaseName == template.DatabaseName.Trim() &&
               saved.Username == template.Username.Trim() &&
               saved.Password == template.Password &&
               saved.SslMode == template.SslMode &&
               saved.ConnectionType == template.ConnectionType &&
               saved.Notes == template.Notes.Trim() &&
               saved.Color == template.Color;
    }
}
