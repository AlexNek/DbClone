using DbClone.Application.Interfaces;
using DbClone.UI.ViewModels;

using FsCheck.Xunit;

using NSubstitute;

using UI.Tests.Fakes;

namespace UI.Tests.Properties;

/// <summary>
/// Feature: unified-connection-manager
/// Property 19: Tab memory within session
/// Validates: Requirements 8.4
/// </summary>
public class TabMemoryTests
{
    /// <summary>
    /// For any tab index (0 or 1) set on a ViewModel, closing it and creating a new ViewModel
    /// with initialTab: 0 SHALL initialize SelectedTabIndex to the last value set before close.
    /// The static s_lastTabIndex field persists across VM instances within a session.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool Tab_memory_persists_across_vm_instances(bool useGroupsTab)
    {
        // Map bool to tab index: false = 0 (Connections), true = 1 (Groups)
        var tabIndex = useGroupsTab ? 1 : 0;

        // Arrange — create first VM and set the tab
        var vm1 = CreateVm(initialTab: 0);
        vm1.SelectedTabIndex = tabIndex;

        // Act — "close" vm1 (let it go out of scope), create vm2 with initialTab: 0
        // When initialTab is 0, the constructor uses s_lastTabIndex (session memory)
        var vm2 = CreateVm(initialTab: 0);

        // Assert — vm2 should remember the last tab set by vm1
        return vm2.SelectedTabIndex == tabIndex;
    }

    private static UnifiedConnectionManagerViewModel CreateVm(int initialTab = 0)
    {
        return new UnifiedConnectionManagerViewModel(
            new InMemoryConnectionStore(),
            new InMemoryConnectionGroupStore(),
            Substitute.For<IConnectionStringService>(),
            Substitute.For<IDatabaseMaintenanceProvider>(),
            Substitute.For<IConnectionImportService>(),
            Substitute.For<IConnectionExportService>(),
            Substitute.For<IBackupEncryptionService>(),
            TestPlatformResolver.Create(),
            initialTab: initialTab);
    }
}
