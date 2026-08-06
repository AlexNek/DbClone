using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WixToolset.BootstrapperApplicationApi;

namespace DbClone.Installer.ViewModels;

/// <summary>
/// Main view model for the installer. Manages the state machine
/// that controls page flow and communicates with the Burn engine.
/// </summary>
public sealed partial class InstallerViewModel : ObservableObject
{
    private readonly IEngine _engine;

    public InstallerViewModel(IEngine engine)
    {
        _engine = engine;

        InstallFolder = GetDefaultInstallFolder();
        WindowTitle = BuildWindowTitle();
        CurrentState = InstallerState.Initializing;
    }

    // --- State Machine ---

    public enum InstallerState
    {
        Initializing,
        Welcome,
        License,
        Directory,
        Progress,
        Maintenance,
        Finish,
        Failed
    }

    [ObservableProperty]
    private InstallerState _currentState;

    /// <summary>
    /// Window caption including the package version being installed, e.g.
    /// "DbClone 2.1.0 Setup", so the user always sees which version this
    /// installer carries.
    /// </summary>
    [ObservableProperty]
    private string _windowTitle = "DbClone Setup";

    [ObservableProperty]
    private string _installFolder;

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    /// <summary>Drives the optional notice line on the welcome page.</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    [ObservableProperty]
    private bool _isExistingInstall;

    /// <summary>
    /// True when DbClone.exe is actually present in the install folder.
    /// False for a registration-only (broken) install, e.g. after the
    /// folder was deleted manually - the maintenance page then offers to
    /// repair or clean up instead of pretending the app is there.
    /// </summary>
    [ObservableProperty]
    private bool _isAppPresent;

    [ObservableProperty]
    private string _installedVersion = string.Empty;

    /// <summary>
    /// Repair is available when this bundle itself is registered or a related
    /// MSI product (same upgrade code) was detected - both can reinstall files.
    /// </summary>
    [ObservableProperty]
    private bool _canRepair;

    /// <summary>
    /// Uninstall is available when something removable was detected: this
    /// bundle, a related MSI, or a related bundle registration (removed via
    /// its cached quiet uninstaller).
    /// </summary>
    [ObservableProperty]
    private bool _canUninstall;

    /// <summary>
    /// Explanation shown on the maintenance page for a registration-only
    /// (broken) install; lists only the actions actually available.
    /// </summary>
    [ObservableProperty]
    private string _maintenanceNotice = string.Empty;

    /// <summary>True when this bundle itself is registered.</summary>
    private bool _bundleInstalled;

    /// <summary>Product code of a detected related MSI (same upgrade code).</summary>
    private string? _relatedMsiProductCode;

    /// <summary>
    /// Identity (provider key) of a detected related bundle registration,
    /// used to locate its ARP entry for uninstalling a leftover registration.
    /// </summary>
    private string? _relatedBundleId;

    /// <summary>
    /// Findings from the related-item events of the current detection pass.
    /// Those events fire before DetectComplete, so they only collect here;
    /// OnDetectComplete resets everything and commits, which keeps re-detects
    /// after an uninstall from showing stale "already installed" state.
    /// </summary>
    private bool _detectedRelatedBundle;
    private string? _detectedBundleVersion;
    private string? _detectedRelatedBundleId;
    private string? _detectedRelatedMsiProductCode;

    /// <summary>Action of the current apply pass (drives finish-page text).</summary>
    private LaunchAction _currentAction = LaunchAction.Install;

    /// <summary>Status line to show after the next detection completes.</summary>
    private string? _pendingDetectMessage;

    [ObservableProperty]
    private bool _licenseAccepted;

    [ObservableProperty]
    private bool _createDesktopShortcut;

    [ObservableProperty]
    private bool _launchAfterInstall = true;

    /// <summary>
    /// Finish-page text, adapted to the applied action (install vs repair).
    /// </summary>
    [ObservableProperty]
    private string _finishMessage = "DbClone has been successfully installed.";

    /// <summary>Launch checkbox only makes sense after an install.</summary>
    [ObservableProperty]
    private bool _showLaunchOption = true;

    public int ExitCode { get; private set; } = 1602; // ERROR_INSTALL_USEREXIT until install succeeds

    // --- Navigation Commands ---

    [RelayCommand]
    private void NavigateNext()
    {
        CurrentState = CurrentState switch
        {
            InstallerState.Welcome => InstallerState.License,
            // Next stays disabled on the license page (XAML); this guard is
            // the defense in depth for keyboard navigation.
            InstallerState.License when !LicenseAccepted => InstallerState.License,
            InstallerState.License => InstallerState.Directory,
            InstallerState.Directory => StartInstall(),
            InstallerState.Finish => CloseInstaller(),
            _ => CurrentState
        };
    }

    [RelayCommand]
    private void NavigateBack()
    {
        CurrentState = CurrentState switch
        {
            // From Welcome, Back returns to the maintenance page when an
            // existing install was detected (the wizard was entered via
            // "Install to a different folder").
            InstallerState.Welcome => IsExistingInstall
                ? InstallerState.Maintenance
                : InstallerState.Welcome,
            InstallerState.License => InstallerState.Welcome,
            InstallerState.Directory => InstallerState.License,
            _ => CurrentState
        };
    }

    [RelayCommand]
    private void Cancel()
    {
        var result = MessageBox.Show(
            "Are you sure you want to cancel the installation?",
            "DbClone Setup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            ExitCode = 1602; // ERROR_INSTALL_USEREXIT
            Application.Current?.MainWindow?.Close();
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select install location",
            InitialDirectory = InstallFolder
        };

        if (dialog.ShowDialog() == true)
        {
            InstallFolder = dialog.FolderName;
        }
    }

    // --- Maintenance Commands ---

    [RelayCommand]
    private void Update()
    {
        // Plan a normal install: Burn's major upgrade replaces the previous
        // MSI, and file overwrite handles legacy Inno installs.
        PlanAction(LaunchAction.Install);
    }

    /// <summary>
    /// Leaves the maintenance page and starts a fresh install wizard,
    /// e.g. to install into a different folder.
    /// </summary>
    [RelayCommand]
    private void FreshInstall()
    {
        CurrentState = InstallerState.Welcome;
    }

    [RelayCommand]
    private async Task Repair()
    {
        if (_bundleInstalled)
        {
            PlanAction(LaunchAction.Repair);
            return;
        }

        if (string.IsNullOrEmpty(_relatedMsiProductCode))
            return;

        // No registered bundle: repair the related MSI product directly
        // through msiexec - the same operation as the MSI's Repair.
        var exitCode = await RunMsiExecAsync($"/f{_relatedMsiProductCode}");
        ReportMsiExecResult(exitCode, "Repair");
        _pendingDetectMessage = exitCode is 0 or 3010
            ? "Repair completed successfully."
            : null;
        _engine.Detect(); // Refresh the maintenance page state.
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        var result = MessageBox.Show(
            Application.Current?.MainWindow,
            "Are you sure you want to uninstall DbClone?",
            "DbClone Setup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        if (_bundleInstalled)
        {
            PlanAction(LaunchAction.Uninstall);
            return;
        }

        if (!string.IsNullOrEmpty(_relatedBundleId))
        {
            // Leftover registration of a related bundle (same upgrade code,
            // different identity): run its cached quiet uninstaller - its
            // chain also removes the MSI it installed.
            ShowUninstallProgress();
            await UninstallRelatedBundleAsync();
            return;
        }

        if (string.IsNullOrEmpty(_relatedMsiProductCode))
            return;

        // No registered bundle: remove the related MSI product directly
        // through msiexec - the same operation as the MSI's Remove.
        ShowUninstallProgress();
        var exitCode = await RunMsiExecAsync($"/x{_relatedMsiProductCode}");
        ReportMsiExecResult(exitCode, "Uninstall");
        var removed = exitCode is 0 or 3010;
        if (removed)
            removed = CleanupLeftoverFiles();
        _pendingDetectMessage = removed
            ? "DbClone was removed from this computer."
            : "The DbClone registration was removed, but some files remain.";
        _engine.Detect(); // Refresh the maintenance page state.
    }

    /// <summary>
    /// Moves the wizard to the progress page with a status line while an
    /// external uninstaller runs. The headless uninstaller needs a UAC
    /// consent to elevate; without this hint the wizard looks frozen and
    /// the secure-desktop prompt is easy to miss.
    /// </summary>
    private void ShowUninstallProgress()
    {
        _currentAction = LaunchAction.Uninstall;
        CurrentState = InstallerState.Progress;
        ProgressPercent = 0;
        StatusMessage = "Uninstalling... Approve the Windows security prompt (UAC) if it appears.";
    }

    /// <summary>
    /// When the MSI registration is already gone, an uninstall only clears
    /// the bundle registration - the installed files remain on disk. Detect
    /// that case and offer to delete the leftover folder so "uninstall"
    /// actually leaves a clean machine.
    /// </summary>
    private bool CleanupLeftoverFiles()
    {
        var exePath = System.IO.Path.Combine(InstallFolder, "DbClone.exe");
        if (!System.IO.File.Exists(exePath))
            return true;

        var cleanup = MessageBox.Show(
            Application.Current?.MainWindow,
            "The uninstaller removed the DbClone registration, but installed files " +
            $"were left behind in:\n\n{InstallFolder}\n\nDelete this folder now?",
            "DbClone Setup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (cleanup != MessageBoxResult.Yes)
            return false;

        try
        {
            System.IO.Directory.Delete(InstallFolder, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Application.Current?.MainWindow,
                $"The leftover folder could not be deleted:\n{ex.Message}\n\n" +
                $"Remove '{InstallFolder}' manually.",
                "DbClone Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>
    /// Removes a leftover related-bundle registration by running the quiet
    /// uninstaller recorded in its Add/Remove Programs entry - the same
    /// command ARP uses. Used when only the registration remains (files and
    /// MSI already gone).
    /// </summary>
    private async Task UninstallRelatedBundleAsync()
    {
        var command = GetRelatedBundleUninstallCommand();
        if (command is null)
        {
            MessageBox.Show(
                Application.Current?.MainWindow,
                "The leftover DbClone registration could not be located for removal.",
                "DbClone Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            CurrentState = InstallerState.Maintenance;
            return;
        }

        System.Diagnostics.Process? process = null;
        try
        {
            var (file, arguments) = SplitCommandLine(command);
            process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(file, arguments));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Application.Current?.MainWindow,
                $"The leftover DbClone uninstaller could not be started:\n{ex.Message}",
                "DbClone Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            CurrentState = InstallerState.Maintenance;
            return;
        }

        if (process is null)
        {
            MessageBox.Show(
                Application.Current?.MainWindow,
                "The leftover DbClone uninstaller could not be started.",
                "DbClone Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            CurrentState = InstallerState.Maintenance;
            return;
        }

        using (process)
        {
            await process.WaitForExitAsync();
            ReportMsiExecResult(process.ExitCode, "Uninstall");
            var removed = process.ExitCode is 0 or 3010;
            if (removed)
                removed = CleanupLeftoverFiles();
            _pendingDetectMessage = removed
                ? "DbClone was removed from this computer."
                : "The DbClone registration was removed, but some files remain.";
        }
        _engine.Detect(); // Refresh the maintenance page state.
    }

    /// <summary>
    /// Reads the quiet uninstall command of the detected related bundle from
    /// its Add/Remove Programs registry entry (per-machine first, then per-user).
    /// </summary>
    private string? GetRelatedBundleUninstallCommand()
    {
        if (string.IsNullOrEmpty(_relatedBundleId))
            return null;

        var keyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + _relatedBundleId;
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(keyPath);
            if (key is null)
                continue;

            if (key.GetValue("QuietUninstallString") is string quiet && quiet.Length > 0)
                return quiet;
            if (key.GetValue("UninstallString") is string plain && plain.Length > 0)
                return plain + " /quiet /norestart";
        }

        return null;
    }

    /// <summary>Splits "file" args into its quoted file and argument parts.</summary>
    private static (string File, string Arguments) SplitCommandLine(string command)
    {
        command = command.Trim();
        if (command.StartsWith("\""))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0)
                return (command[1..end], command[(end + 1)..].TrimStart());
        }

        var space = command.IndexOf(' ');
        return space < 0
            ? (command, string.Empty)
            : (command[..space], command[(space + 1)..]);
    }
    /// <summary>
    /// Runs msiexec with the given arguments and waits for it to finish.
    /// UseShellExecute lets Windows show the UAC elevation prompt.
    /// </summary>
    private static async Task<int> RunMsiExecAsync(string arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("msiexec.exe", arguments)
        {
            UseShellExecute = true
        };

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
            return -1;

        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static void ReportMsiExecResult(int exitCode, string operation)
    {
        // 0 = success, 1602 = user cancelled, 3010 = reboot required.
        if (exitCode is 0 or 1602 or 3010)
            return;

        MessageBox.Show(
            $"{operation} did not complete (msiexec exit code {exitCode}).",
            "DbClone Setup",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    // --- Engine Event Handlers ---

    public void OnDetectComplete(DetectCompleteEventArgs args)
    {
        if (args.Status >= 0)
        {
            // Reset everything from the previous detection pass first: a
            // re-detect after uninstall/repair must reflect the current
            // machine state, not keep stale "already installed" flags.
            _bundleInstalled = false;
            _relatedMsiProductCode = _detectedRelatedMsiProductCode;
            _relatedBundleId = _detectedRelatedBundleId;
            var hadRelatedBundle = _detectedRelatedBundle;
            _detectedRelatedBundle = false;
            _detectedRelatedBundleId = null;
            _detectedRelatedMsiProductCode = null;
            IsExistingInstall = false;
            IsAppPresent = false;
            InstalledVersion = _detectedBundleVersion ?? string.Empty;
            _detectedBundleVersion = null;

            // The bundle resolves InstallFolder via its searches (Inno legacy
            // path or the Program Files default) during detection - pick that
            // up instead of always defaulting to Program Files.
            try
            {
                var resolved = _engine.GetVariableString("InstallFolder");
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    // InstallFolder is declared Type="formatted" in the bundle,
                    // so GetVariableString can return the unresolved literal
                    // (e.g. "[ProgramFiles64Folder]DbClone"); FormatString
                    // resolves any [..] placeholders against current variables.
                    resolved = _engine.FormatString(resolved) ?? resolved;
                    InstallFolder = resolved.TrimEnd('\\');
                }
            }
            catch
            {
                // Variable not defined - keep the default.
            }

            // Repair can restore files when this bundle itself is registered
            // (Burn maintains it) or a related MSI product was detected
            // (maintained through msiexec). Uninstall additionally works for
            // a leftover related-bundle registration via its ARP quiet
            // uninstaller.
            try
            {
                _bundleInstalled = _engine.GetVariableNumeric("WixBundleInstalled") != 0;
            }
            catch
            {
                // Variable not defined - bundle not registered.
            }

            CanRepair = _bundleInstalled || !string.IsNullOrEmpty(_relatedMsiProductCode);
            CanUninstall = CanRepair || !string.IsNullOrEmpty(_relatedBundleId);

            // Ground truth: does the application actually exist on disk in
            // the resolved folder? Also fills in the version from the exe
            // metadata when Burn's detection only reported "Unknown" (orphan
            // MSI) or found nothing at all (legacy Inno install).
            var exePath = System.IO.Path.Combine(InstallFolder, "DbClone.exe");
            IsAppPresent = System.IO.File.Exists(exePath);
            if (IsAppPresent)
            {
                IsExistingInstall = true;
                if (string.IsNullOrEmpty(InstalledVersion) || InstalledVersion == "Unknown")
                {
                    InstalledVersion = ReadExeVersion(exePath);
                }
            }

            // A stale Inno/legacy registry entry can point InstallFolder at
            // a folder that no longer exists. Without anything registered
            // that path is meaningless - fall back to the default location
            // (locally and in the engine, so the MSI receives it too).
            if (!IsAppPresent && !CanUninstall && !System.IO.Directory.Exists(InstallFolder))
            {
                InstallFolder = GetDefaultInstallFolder();
                _engine.SetVariableString("InstallFolder", InstallFolder, false);
            }

            // Registration without files on disk (folder deleted manually or
            // a partial uninstall) still counts as an existing install, but
            // the maintenance page shows the repair/cleanup hint instead of
            // pretending the app is present.
            if (CanUninstall || hadRelatedBundle)
                IsExistingInstall = true;

            if (string.IsNullOrEmpty(InstalledVersion))
                InstalledVersion = "Unknown";

            // Registration without files on disk: explain what happened and
            // list only the actions that are actually available.
            MaintenanceNotice = IsExistingInstall && !IsAppPresent
                ? BuildMaintenanceNotice()
                : string.Empty;

            // Existing install: show the maintenance page (Update / Repair /
            // Uninstall) instead of blindly running the install wizard into
            // the occupied folder. Fresh machines go straight to Welcome.
            CurrentState = IsExistingInstall
                ? InstallerState.Maintenance
                : InstallerState.Welcome;

            if (_pendingDetectMessage is not null)
            {
                StatusMessage = _pendingDetectMessage;
                _pendingDetectMessage = null;
            }
        }
        else
        {
            StatusMessage = $"Detection failed: 0x{args.Status:X8}";
            CurrentState = InstallerState.Failed;
        }
    }

    /// <summary>
    /// Broken-install notice text; mentions only the actions the current
    /// machine state actually offers, so the page never promises buttons
    /// that are hidden.
    /// </summary>
    private string BuildMaintenanceNotice()
    {
        var notice = $"DbClone is registered on this computer (version {InstalledVersion}), " +
                     "but its files are missing - the folder was removed outside of Setup.";

        if (CanRepair && CanUninstall)
            notice += " Use Repair to restore the files, or Uninstall to remove the registration.";
        else if (CanRepair)
            notice += " Use Repair to restore the files.";
        else if (CanUninstall)
            notice += " Use Uninstall to remove the leftover registration, or Update to install this version.";
        else
            notice += " Use Update to install this version.";

        return notice;
    }

    private static string ReadExeVersion(string exePath)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            var product = info.ProductVersion;
            if (!string.IsNullOrWhiteSpace(product))
            {
                // GitVersion appends "+Branch...Sha..." metadata -
                // show only the plain version part.
                var plus = product.IndexOf('+');
                if (plus > 0)
                    product = product[..plus];
                return product;
            }

            return info.FileVersion ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    public void OnDetectRelatedBundle(DetectRelatedBundleEventArgs args)
    {
        // Collect only; OnDetectComplete resets and commits, so an uninstall
        // that removes the registration is reflected on the next detect.
        _detectedRelatedBundle = true;
        _detectedBundleVersion = args.Version?.ToString();
        _detectedRelatedBundleId = args.ProductCode;
    }

    /// <summary>
    /// Fired for MSI products sharing the upgrade code (e.g. an orphan MSI
    /// installed directly). Collected here; OnDetectComplete commits it so
    /// the maintenance page reflects the detected state.
    /// </summary>
    public void OnDetectRelatedMsiPackage(DetectRelatedMsiPackageEventArgs args)
    {
        if (!string.IsNullOrEmpty(args.ProductCode))
            _detectedRelatedMsiProductCode = args.ProductCode;
    }

    public void OnPlanComplete(PlanCompleteEventArgs args)
    {
        if (args.Status >= 0)
        {
            // Burn v5 requires a real parent window handle for interactive
            // runs (Apply with IntPtr.Zero fails with 0x80070057).
            var hwnd = Application.Current?.MainWindow is null
                ? IntPtr.Zero
                : new System.Windows.Interop.WindowInteropHelper(Application.Current.MainWindow).Handle;
            try
            {
                _engine.Apply(hwnd);
            }
            catch (Exception ex)
            {
                // Apply never started - show the failure instead of hanging
                // on a progress bar that will never move.
                StatusMessage = $"Installation could not start: {ex.Message}";
                CurrentState = InstallerState.Failed;
            }
        }
        else
        {
            StatusMessage = $"Planning failed: 0x{args.Status:X8}";
            CurrentState = InstallerState.Failed;
        }
    }

    public void OnApplyBegin(ApplyBeginEventArgs args)
    {
        CurrentState = InstallerState.Progress;
        ProgressPercent = 0;
        // Match the wording to the actual pass - PlanAction already set the
        // right message, but ApplyBegin fires later and must not reset it
        // to "Installing..." during an uninstall or repair.
        StatusMessage = _currentAction switch
        {
            LaunchAction.Uninstall => "Uninstalling... Approve the Windows security prompt (UAC) if it appears.",
            LaunchAction.Repair => "Repairing...",
            LaunchAction.UpdateReplace => "Updating...",
            _ => "Installing..."
        };
    }

    public void OnApplyComplete(ApplyCompleteEventArgs args)
    {
        var action = _currentAction;

        if (args.Status >= 0)
        {
            ExitCode = 0;

            if (action == LaunchAction.Uninstall)
            {
                // Burn only removes what is still registered: when the MSI
                // product registration was already gone, the uninstall plan
                // executes nothing and the files stay on disk - offer to
                // clean them up explicitly.
                var fullyRemoved = CleanupLeftoverFiles();

                // Re-detect so the wizard reflects the post-uninstall state:
                // registration gone -> Welcome (with a removal notice);
                // leftovers detected -> back to the maintenance page.
                StatusMessage = "Uninstall completed.";
                _pendingDetectMessage = fullyRemoved
                    ? "DbClone was removed from this computer."
                    : "The DbClone registration was removed, but some files remain.";
                _engine.Detect();
                return;
            }

            StatusMessage = action == LaunchAction.Repair
                ? "Repair completed successfully."
                : "Installation completed successfully.";
            FinishMessage = action == LaunchAction.Repair
                ? "DbClone has been repaired successfully."
                : "DbClone has been successfully installed.";
            ShowLaunchOption = action != LaunchAction.Repair;
            CurrentState = InstallerState.Finish;
        }
        else
        {
            StatusMessage = action switch
            {
                LaunchAction.Uninstall => $"Uninstall failed: 0x{args.Status:X8}",
                LaunchAction.Repair => $"Repair failed: 0x{args.Status:X8}",
                _ => $"Installation failed: 0x{args.Status:X8}"
            };
            CurrentState = InstallerState.Failed;
            ExitCode = args.Status;
        }
    }

    public void OnExecuteProgress(ExecuteProgressEventArgs args)
    {
        ProgressPercent = args.OverallPercentage;
    }

    // --- Private Helpers ---

    private InstallerState StartInstall()
    {
        // Warn when the chosen folder already contains files (e.g. a legacy
        // Inno install or an unrelated application) so the user can pick
        // another location before anything is written.
        if (System.IO.Directory.Exists(InstallFolder)
            && System.IO.Directory.EnumerateFileSystemEntries(InstallFolder).Any())
        {
            var confirm = MessageBox.Show(
                $"The folder '{InstallFolder}' already exists and is not empty.\n\n" +
                "Existing files in this folder may be overwritten or mixed with " +
                "the new installation.\n\nDo you want to continue anyway?",
                "DbClone Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return InstallerState.Directory;
        }

        // Set variables in the engine before planning
        _engine.SetVariableString("InstallFolder", InstallFolder, false);
        _engine.SetVariableNumeric("InstallDesktopShortcut", CreateDesktopShortcut ? 1 : 0);
        _engine.SetVariableNumeric("LaunchAfterInstall", LaunchAfterInstall ? 1 : 0);

        PlanAction(LaunchAction.Install);
        return InstallerState.Progress;
    }

    private void PlanAction(LaunchAction action)
    {
        _currentAction = action;
        CurrentState = InstallerState.Progress;
        StatusMessage = action switch
        {
            LaunchAction.Install => "Installing...",
            LaunchAction.Repair => "Repairing...",
            // Burn elevates for per-machine removal; hint at the consent
            // prompt so a pending UAC dialog does not look like a hang.
            LaunchAction.Uninstall => "Uninstalling... Approve the Windows security prompt (UAC) if it appears.",
            LaunchAction.UpdateReplace => "Updating...",
            _ => "Working..."
        };
        _engine.Plan(action);
    }

    private InstallerState CloseInstaller()
    {
        // Only an install leaves an application to launch - never start
        // DbClone after a repair or uninstall pass.
        if (LaunchAfterInstall && _currentAction == LaunchAction.Install)
        {
            LaunchApplication();
        }

        Application.Current?.MainWindow?.Close();
        return InstallerState.Finish;
    }

    private void LaunchApplication()
    {
        var exePath = System.IO.Path.Combine(InstallFolder, "DbClone.exe");
        if (System.IO.File.Exists(exePath))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            });
        }
    }

    private static string GetDefaultInstallFolder()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return System.IO.Path.Combine(programFiles, "DbClone");
    }

    /// <summary>
    /// Composes the window caption from the bundle version Burn read from
    /// the manifest (WixBundleVersion = Bundle/@Version). Falls back to the
    /// plain caption when the variable is unavailable.
    /// </summary>
    private string BuildWindowTitle()
    {
        try
        {
            var version = _engine.GetVariableString("WixBundleVersion");
            if (!string.IsNullOrWhiteSpace(version))
                return $"DbClone {version} Setup";
        }
        catch
        {
            // Variable not defined yet - keep the generic caption.
        }

        return "DbClone Setup";
    }
}
