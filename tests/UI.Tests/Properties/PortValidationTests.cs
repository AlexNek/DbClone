using DbClone.Application.Interfaces;
using DbClone.UI.ViewModels;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using NSubstitute;

using UI.Tests.Fakes;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager, Property 12: Port validation rejects out-of-range values
/// Validates: Requirements 4.5
/// </summary>
public class PortValidationTests
{
    /// <summary>
    /// Generator for integers outside the valid port range [1, 65535].
    /// Produces values that are zero/negative or greater than 65535.
    /// </summary>
    public static Arbitrary<int> InvalidPortArbitrary()
    {
        var gen = Gen.OneOf(
            Gen.Choose(int.MinValue / 1000, 0), // Negative or zero
            Gen.Choose(65536, int.MaxValue / 1000) // Above max port
        );
        return Arb.From(gen);
    }

    /// <summary>
    /// Non-numeric port strings SHALL also be rejected with a validation error.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool Non_numeric_port_rejects_save_and_shows_error(NonEmptyString nonNumeric)
    {
        // Skip if the string happens to be a valid integer in range
        if (int.TryParse(nonNumeric.Item, out var parsed) && parsed >= 1 && parsed <= 65535)
            return true; // trivially passes — not an invalid port

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

        // Act — set non-numeric port and attempt to save
        vm.NewConnectionCommand.Execute(null);
        vm.FormHost = "localhost";
        vm.FormDatabaseName = "testdb";
        vm.FormPort = nonNumeric.Item;

        vm.SaveConnectionCommand.Execute(null);

        // Assert — validation error is shown and store remains empty
        return !string.IsNullOrEmpty(vm.PortValidationError) &&
               connectionStore.GetAll().Count == 0;
    }

    /// <summary>
    /// For any integer value outside the range [1, 65535], the ViewModel SHALL indicate
    /// a validation error and SHALL NOT allow the connection to be saved with that port value.
    /// </summary>
    [Property(Arbitrary = [typeof(PortValidationTests)], MaxTest = 100)]
    public bool Out_of_range_port_rejects_save_and_shows_error(int invalidPort)
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

        // Act — set invalid port and attempt to save
        vm.NewConnectionCommand.Execute(null);
        vm.FormHost = "localhost";
        vm.FormDatabaseName = "testdb";
        vm.FormPort = invalidPort.ToString();

        vm.SaveConnectionCommand.Execute(null);

        // Assert — validation error is shown and store remains empty
        return !string.IsNullOrEmpty(vm.PortValidationError) &&
               connectionStore.GetAll().Count == 0;
    }
}
