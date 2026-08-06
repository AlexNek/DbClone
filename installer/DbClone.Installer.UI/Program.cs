using WixToolset.BootstrapperApplicationApi;

namespace DbClone.Installer;

/// <summary>
/// Entry point for the custom WPF bootstrapper application.
/// WiX v5 runs bootstrapper applications as out-of-process EXEs.
/// </summary>
internal class Program
{
    // NOTE: no [STAThread] here. Burn's native loader must initialize COM
    // on the main thread itself (RPC_E_CHANGED_MODE otherwise). The WPF
    // wizard runs on a dedicated STA thread created in Run().
    private static int Main()
    {
        var application = new DbCloneBootstrapperApplication();
        ManagedBootstrapperApplication.Run(application);
        return 0;
    }
}
