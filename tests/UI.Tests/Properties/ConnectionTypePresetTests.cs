using DbClone.Application.Interfaces;
using DbClone.Application.Platforms;
using DbClone.UI.ViewModels;

using NSubstitute;

using UI.Tests.Fakes;

using Xunit;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager, Property 11: Connection type preset application
/// Validates: Requirements 4.3
/// </summary>
public class ConnectionTypePresetTests
{
    /// <summary>
    /// For any platform display name, when FormConnectionType is set to that value,
    /// FormPort SHALL equal the .platform default port and FormSslMode
    /// SHALL equal the .platform default SSL mode.
    /// </summary>
    [Theory]
    [InlineData("postgresql")]
    [InlineData("supabase")]
    [InlineData("neon")]
    [InlineData("aiven")]
    [InlineData("azure")]
    [InlineData("unknown_platform")]
    public void Setting_connection_type_applies_preset_port_and_ssl(string platformId)
    {
        // Arrange — create VM with empty stores
        var connectionStore = new InMemoryConnectionStore();
        var groupStore = new InMemoryConnectionGroupStore();
        var connectionStringService = Substitute.For<IConnectionStringService>();
        var maintenanceProvider = Substitute.For<IDatabaseMaintenanceProvider>();
        var platformResolver = TestPlatformResolver.Create();

        var vm = new UnifiedConnectionManagerViewModel(
            connectionStore,
            groupStore,
            connectionStringService,
            maintenanceProvider,
            Substitute.For<IConnectionImportService>(),
            Substitute.For<IConnectionExportService>(),
            Substitute.For<IBackupEncryptionService>(),
            platformResolver);

        // Act — set the connection type
        vm.FormConnectionType = platformId;

        // Assert — port and SSL mode match the .platform defaults
        var defaults = platformResolver.GetConnectionDefaults("postgresql", platformId);
        Assert.Equal(defaults.Port.ToString(), vm.FormPort);
        Assert.Equal(defaults.SslMode, vm.FormSslMode);
    }
}
