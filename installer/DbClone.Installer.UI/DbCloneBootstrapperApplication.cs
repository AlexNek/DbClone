using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DbClone.Installer.ViewModels;
using DbClone.Installer.Views;
using WixToolset.BootstrapperApplicationApi;

namespace DbClone.Installer;

/// <summary>
/// Custom WPF bootstrapper application for DbClone installer.
/// Inherits from BootstrapperApplication and overrides Run() to show
/// the custom WPF window and interact with the Burn engine.
/// </summary>
public sealed class DbCloneBootstrapperApplication : BootstrapperApplication
{
    private Dispatcher? _dispatcher;
    private InstallerViewModel? _viewModel;
    private IBootstrapperCommand? _command;

    /// <summary>
    /// True when the installer was launched with /relaunch (used by the app's
    /// auto-update flow); the app is restarted after a successful install.
    /// </summary>
    private bool _relaunchAfterInstall;

    /// <summary>
    /// Called by the engine before <see cref="Run"/>; captures the command
    /// (requested action and display level) and the engine reference.
    /// </summary>
    protected override void OnCreate(CreateEventArgs args)
    {
        _command = args.Command;

        // Detect /relaunch (used by the app's autoupdate flow). Burn surfaces
        // the user-supplied switches it did not consume via
        // Command.CommandLine; the raw Environment process args are lost when
        // Burn relaunches itself elevated, so check both sources.
        _relaunchAfterInstall =
            ContainsRelaunch(args.Command?.CommandLine)
            || ContainsRelaunch(string.Join(" ", Environment.GetCommandLineArgs()));

        base.OnCreate(args);
    }

    private static bool ContainsRelaunch(string? text) =>
        text is not null
        && text.IndexOf("/relaunch", StringComparison.OrdinalIgnoreCase) >= 0;

    protected override void Run()
    {
        // Silent/unattended launches (autoupdate with /passive or /quiet) and
        // maintenance actions from Add/Remove Programs (uninstall/repair):
        // drive the engine directly without showing the wizard.
        var requestedAction = _command?.Action ?? LaunchAction.Install;
        var isInstallAction = requestedAction == LaunchAction.Unknown
            || requestedAction == LaunchAction.Install
            || requestedAction == LaunchAction.UpdateReplace
            || requestedAction == LaunchAction.Help;

        if (_command is null || _command.Display != Display.Full || !isInstallAction)
        {
            RunSilent(requestedAction);
            return;
        }

        RunWizard();
    }

    /// <summary>
    /// Shows the WPF wizard. Burn hosts <see cref="Run"/> on an MTA thread,
    /// but WPF requires an STA thread, so the entire UI lives on a
    /// dedicated STA thread; the calling thread blocks until it exits.
    /// </summary>
    private void RunWizard()
    {
        var uiDone = new System.Threading.ManualResetEventSlim(false);

        var uiThread = new System.Threading.Thread(() =>
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;

                // Create WPF Application to load theme resources
                var app = new App();
                app.InitializeComponent();

                _viewModel = new InstallerViewModel(engine);

                // Subscribe to engine events to forward to the view model on the UI thread
                DetectComplete += (_, args) => _dispatcher.Invoke(() => _viewModel.OnDetectComplete(args));
                DetectRelatedBundle += (_, args) => _dispatcher.Invoke(() => _viewModel.OnDetectRelatedBundle(args));
                DetectRelatedMsiPackage += (_, args) => _dispatcher.Invoke(() => _viewModel.OnDetectRelatedMsiPackage(args));
                PlanComplete += (_, args) => _dispatcher.Invoke(() => _viewModel.OnPlanComplete(args));
                ApplyBegin += (_, args) => _dispatcher.Invoke(() => _viewModel.OnApplyBegin(args));
                ApplyComplete += (_, args) => _dispatcher.Invoke(() => _viewModel.OnApplyComplete(args));
                ExecuteProgress += (_, args) => _dispatcher.Invoke(() => _viewModel.OnExecuteProgress(args));

                var window = new InstallerWindow { DataContext = _viewModel };

                // MainWindow must be set manually (no Application.Run);
                // its hwnd is passed to engine.Apply as the parent window.
                app.MainWindow = window;

                window.Closed += (_, _) =>
                {
                    engine.Quit(_viewModel.ExitCode);
                    _dispatcher.InvokeShutdown();
                };

                window.Show();

                // Kick off detection to determine installed state
                engine.Detect();

                // Run WPF message loop (blocks until shutdown)
                Dispatcher.Run();
            }
            finally
            {
                uiDone.Set();
            }
        })
        {
            Name = "DbCloneInstallerUI",
            IsBackground = false,
        };
        uiThread.SetApartmentState(System.Threading.ApartmentState.STA);
        uiThread.Start();
        uiDone.Wait();
    }

    /// <summary>
    /// Headless flow for passive/quiet/silent command lines. Detects, plans
    /// the requested action (install by default) and applies, then quits with
    /// the engine result. No wizard is shown, but an invisible owner window is
    /// created because engine.Apply rejects a NULL parent hwnd even in quiet
    /// mode.
    /// </summary>
    private void RunSilent(LaunchAction requestedAction)
    {
        var action = requestedAction;
        if (action == LaunchAction.Unknown || action == LaunchAction.Help)
            action = LaunchAction.Install;

        var plannedAction = action;

        // Invisible owner window on a dedicated STA thread: WPF windows need
        // STA, and Burn hosts Run() on an MTA thread.
        Dispatcher? silentDispatcher = null;
        var hwnd = System.IntPtr.Zero;
        var ready = new System.Threading.ManualResetEventSlim(false);

        var staThread = new System.Threading.Thread(() =>
        {
            silentDispatcher = Dispatcher.CurrentDispatcher;

            var owner = new Window
            {
                Width = 1,
                Height = 1,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            owner.Show();
            owner.Hide(); // stays invisible, but the hwnd remains valid

            hwnd = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            ready.Set();

            Dispatcher.Run();
            owner.Close();
        })
        {
            Name = "DbCloneInstallerSilent",
            IsBackground = false
        };
        staThread.SetApartmentState(System.Threading.ApartmentState.STA);
        staThread.Start();
        ready.Wait();

        DetectComplete += (_, args) =>
        {
            if (args.Status >= 0)
                engine.Plan(plannedAction);
            else
            {
                silentDispatcher?.InvokeShutdown();
                engine.Quit(args.Status);
            }
        };

        PlanComplete += (_, args) =>
        {
            if (args.Status >= 0)
                engine.Apply(hwnd);
            else
            {
                silentDispatcher?.InvokeShutdown();
                engine.Quit(args.Status);
            }
        };

        ApplyComplete += (_, args) =>
        {
            var relog = Path.Combine(Path.GetTempPath(), "dbclone-relaunch.log");
            try { File.AppendAllText(relog, $"{DateTime.Now:O} ApplyComplete status={args.Status} relaunch={_relaunchAfterInstall}\n"); }
            catch { /* diagnostic only */ }

            if (args.Status >= 0 && _relaunchAfterInstall)
                RelaunchApplication();

            silentDispatcher?.InvokeShutdown();
            engine.Quit(args.Status);
        };

        engine.Detect();

        // Block Run() until the silent pump finishes.
        staThread.Join();
    }

    /// <summary>
    /// Restarts the installed application after a successful silent update.
    /// Launched via cmd start so the app is fully detached from the
    /// bootstrapper's process/job objects and survives engine.Quit().
    /// Best-effort: failures never affect the installer exit code.
    /// </summary>
    private void RelaunchApplication()
    {
        var log = Path.Combine(Path.GetTempPath(), "dbclone-relaunch.log");
        try
        {
            var folder = engine.GetVariableString("InstallFolder");
            // InstallFolder is declared Type="formatted", so GetVariableString
            // can return an unresolved literal (e.g. "[ProgramFiles64Folder]DbClone");
            // FormatString resolves any [..] placeholders against current variables.
            if (!string.IsNullOrEmpty(folder))
                folder = engine.FormatString(folder) ?? folder;

            var exePath = string.IsNullOrEmpty(folder)
                              ? ""
                              : Path.Combine(folder.TrimEnd('\\'), "DbClone.exe");
            File.AppendAllText(log,
                $"{DateTime.Now:O} relaunch: folder='{folder}' exe='{exePath}' exists={File.Exists(exePath)}\n");

            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return;

            var dir = Path.GetDirectoryName(exePath);
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"\" \"{exePath}\"",
                WorkingDirectory = dir,
                UseShellExecute = true,
                CreateNoWindow = true
            };
            Process.Start(psi);
            File.AppendAllText(log, $"{DateTime.Now:O} relaunch started via cmd.\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(log, $"{DateTime.Now:O} relaunch ERROR: {ex}\n");
        }
    }
}
