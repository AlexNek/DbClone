using System.IO;
using System.Windows;

using DbClone.Application.Adapters;
using DbClone.Application.Catalog;
using DbAdapter.Definitions;
using DbClone.Application.Compare.Comparers;
using DbClone.Application.Comparison;
using DbClone.Application.Copy;
using DbClone.Application.Enums;
using DbClone.Application.Interchange;
using DbClone.Application.Interfaces;
using DbClone.Application.Relationship;
using DbClone.Application.TableFilter;
using DbClone.Application.Targets;
using DbClone.UI.Logging;
using DbClone.UI.Services;
using DbClone.UI.Services.Relationship;
using DbClone.UI.ViewModels;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;

using Wpf.Ui.Appearance;

namespace DbClone.UI;

public partial class App : System.Windows.Application, IDisposable
{
    private static bool _unhandledReported;

    private bool _disposed;

    private IHost? _host;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _host?.Dispose();
        _host = null;

        GC.SuppressFinalize(this);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("**** DbClone shutting down ****");
        Log.Debug("OnExit: stopping host and disposing services");

        RelationshipViewFile.TryDelete();

        if (_host is not null)
        {
            _host.StopAsync().GetAwaiter().GetResult();
        }

        Dispose();

        Log.Debug("OnExit: flushing and closing Serilog");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // Apply saved theme before showing any window
            var settingsService = new SettingsService();
            var savedTheme = settingsService.Load().Theme;
            var theme = savedTheme switch
                {
                    EThemeMode.Light => ApplicationTheme.Light,
                    EThemeMode.Dark => ApplicationTheme.Dark,
                    _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
                             ? ApplicationTheme.Dark
                             : ApplicationTheme.Light
                };
            ApplicationThemeManager.Apply(theme);

            // Set up Serilog BEFORE building the host
            Log.Logger = LoggingConfiguration.CreateLogger();

            // A UI-thread exception nobody caught terminates the process with no window and no log
            // line, so the app simply vanishes — the crash this handler turns into something findable.
            DispatcherUnhandledException += (_, args) => ReportUnhandled(args.Exception);

            // D10: the fallback's single file never outlives the session that wrote it.
            RelationshipViewFile.TryDelete();

            _host = BuildHost(settingsService);

            Log.Information("**** DbClone starting up ****");
            _host.StartAsync().GetAwaiter().GetResult();

            var mainWindow = ActivatorUtilities.CreateInstance<MainWindow>(
                _host.Services,
                _host.Services.GetRequiredService<MainViewModel>(),
                _host.Services.GetRequiredService<ISettingsService>());
            mainWindow.Show();

            // Silent background update check (no error dialogs on failure)
            var updateService = _host.Services.GetRequiredService<IUpdateService>();
            Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
                Dispatcher.Invoke(() => updateService.CheckForUpdates(reportErrors: false)));

            Log.Information("**** DbClone startup complete ****");
        }
        catch (Exception ex)
        {
            var crashLog = WriteCrashLog(ex, $"CRASH at {DateTime.Now:O}");

            var logDir = LoggingConfiguration.GetLogDirectory();
            var msg =
                $"DbClone encountered a critical error and cannot start.\n\n{ex.GetType().Name}: {ex.Message}";
            if (logDir is not null)
                msg += $"\n\nApplication logs: {logDir}";
            msg += $"\nCrash details: {crashLog}";

            MessageBox.Show(msg, "DbClone Crash");
            Shutdown(1);
        }
    }

    /// <summary>
    /// Records what killed the process before it dies. The exception is not marked handled: after an
    /// uncaught exception the UI state is undefined, so the process still ends — but now with a logged
    /// trace, a readable crash file and one window that says so instead of a silent disappearance.
    /// </summary>
    private static void ReportUnhandled(Exception exception)
    {
        if (_unhandledReported) return;
        _unhandledReported = true;

        Log.Error(exception, "Unhandled exception on the UI thread");

        var crashLog = WriteCrashLog(exception, $"CRASH (unhandled) at {DateTime.Now:O}");
        MessageBox.Show(
            $"DbClone hit an unexpected error and has to close.\n\n{exception.GetType().Name}: {exception.Message}\n\nCrash details: {crashLog}",
            "DbClone Crash");
    }

    private static string WriteCrashLog(Exception exception, string heading)
    {
        var crashDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolder.Name);
        Directory.CreateDirectory(crashDir);
        var crashLog = Path.Combine(crashDir, "crash.log");
        File.WriteAllText(crashLog, $"{heading}\n{exception}");
        return crashLog;
    }

    private static IHost BuildHost(SettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        return Host.CreateDefaultBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .UseEnvironment(
#if DEBUG
                "Development"
#else
                "Production"
#endif
            )
.ConfigureLogging(logging =>
    {
        // The host's logging factory is replaced, not extended: without this the
        // services' own ILogger lines never reached the Serilog file and existed
        // only on the console and in the Windows event log. That event-log
        // provider is dropped with the rest because it throws when the app may
        // not open its source — a log call that throws aborts whatever it was
        // reporting, which is how an update check once ended with no result.
        logging.ClearProviders();
        logging.AddSerilog(Log.Logger, dispose: false);
    })
            .ConfigureServices((_, services) =>
                {
                    // The plugin catalog is the single route from a connection to an adapter.
                    // It scans the publish folder's plugins/ directory; the engine is
                    // discovered there, never registered by name in this file.
                    services.AddSingleton<IAdapterCatalog>(sp =>
                        new DatabaseAdapterCatalog(
                            Path.Combine(AppContext.BaseDirectory, "plugins"),
                            sp.GetRequiredService<ILoggerFactory>()));

                    // Connection-text import/export orchestration is engine-neutral and lives in
                    // the application; it is built over the union of every adapter's formats.
                    services.AddSingleton<IConnectionImportService>(sp =>
                        new DbClone.Application.Services.ConnectionImportService(
                            sp.GetRequiredService<IAdapterCatalog>().Formats,
                            sp.GetRequiredService<ILogger<DbClone.Application.Services.ConnectionImportService>>()));
                    services.AddSingleton<IConnectionExportService>(sp =>
                        new DbClone.Application.Services.ConnectionExportService(
                            sp.GetRequiredService<IAdapterCatalog>().Formats,
                            sp.GetRequiredService<ILogger<DbClone.Application.Services.ConnectionExportService>>()));

                    services.AddInterchangeServices();
                    services.AddSingleton<WpfDialogService>();
                    services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<WpfDialogService>());
                    services.AddSingleton<IConfirmationService>(sp => sp.GetRequiredService<WpfDialogService>());
                    services.AddSingleton<IConnectionStore, ConnectionStore>();
                    services.AddSingleton<IConnectionGroupStore, ConnectionGroupStore>();
                    services.AddSingleton<ITableSelectionPresetStore, TableSelectionPresetStore>();
                    services.AddSingleton<ITableSelectionService, TableSelectionService>();
                    services.AddSingleton<ITableFilterApplier, TableFilterApplier>();
                    services.AddSingleton<ITableSelectionPresetNameValidator, TableSelectionPresetNameValidator>();
                    services.AddSingleton<IBackupEncryptionService, BackupEncryptionService>();
                    services.AddSingleton<IFileService, FileService>();
                    services.AddSingleton<DatabaseTargetFactory>();

                    // Store/Restore workflow services
                    services.AddTransient<StoreRestore.Workflows.IWorkflowService<StoreRestore.Workflows.StoreRequest, StoreRestore.Workflows.StoreResult>, StoreRestore.Workflows.StoreWorkflowService>();
                    services.AddTransient<StoreRestore.Workflows.IWorkflowService<StoreRestore.Workflows.RestoreRequest, StoreRestore.Workflows.RestoreResult>, StoreRestore.Workflows.RestoreWorkflowService>();
                    services.AddSingleton<StoreRestore.Handlers.IWorkflowResultHandler, StoreRestore.Handlers.WorkflowResultHandler>();
                    services.AddSingleton<StoreRestore.Validators.IWorkflowValidator, StoreRestore.Validators.WorkflowValidator>();
                    services.AddSingleton<StoreRestore.Services.ICatalogPathResolver, StoreRestore.Services.CatalogPathResolver>();

                    // Feature 11: the relationship view. One graph service reads a catalog folder or a live
                    // database; one builder renders it; one presenter shows it. No view writes a file — the
                    // document is a string in memory, and only the store-time picture touches disk.
                    // A missing registration here is a startup failure, not a feature that fails when clicked.
                    services.AddSingleton<IRelationshipGraphService, RelationshipGraphService>();
                    services.AddSingleton<Services.CatalogGraphAnalyzer>();
                    services.AddSingleton<Services.GraphLayout.GraphLayoutEngine>();
                    services.AddSingleton<Services.CatalogGraphHtmlRenderer>();
                    services.AddSingleton<RelationshipDocumentBuilder>();
                    services.AddSingleton<StoreRestore.Services.RelationshipPictureWriter>();
                    services.AddSingleton<Services.IHtmlFileOpener, Services.WpfHtmlFileOpener>();

                    // How a document is shown is decided once per request: the in-app window when the
                    // runtime answers and none is open yet, the browser otherwise (D4).
                    services.AddSingleton<BrowserRelationshipPresenter>();
                    services.AddSingleton<WebViewRelationshipPresenter>();
                    services.AddSingleton<IInAppRelationshipPresenter>(
                        sp => sp.GetRequiredService<WebViewRelationshipPresenter>());
                    services.AddSingleton<IWebViewRuntimeProbe, WebViewRuntimeProbe>();
                    services.AddSingleton<IRelationshipViewPresenter>(sp => new RelationshipViewPresenterResolver(
                        sp.GetRequiredService<IWebViewRuntimeProbe>(),
                        sp.GetRequiredService<IInAppRelationshipPresenter>(),
                        sp.GetRequiredService<BrowserRelationshipPresenter>()));
                    services.AddSingleton<StoreRestore.Handlers.IRelationshipViewHandler, StoreRestore.Handlers.RelationshipViewHandler>();

                    services.AddTransient<ITableCatalogService, TableCatalogService>();
                    services.AddTransient<IComparisonService, ComparisonService>();
                    services.AddTransient<ICopyWorkflow, CopyWorkflow>();
                    services.AddSingleton<IPlatformService, DbClone.Application.Services.PlatformService>();
                    services.AddSingleton<IConnectionTextService, DbClone.Application.Services.ConnectionTextService>();
                    services.AddSingleton<IServerAdminService, DbClone.Application.Services.ServerAdminService>();

                    // Provider-neutral model comparers — OCP: add new ones here without editing
                    // ComparisonService. The engine-specific comparer (table DDL) is contributed by
                    // the adapter through IDatabaseAdapter.CreateModelComparers(), not registered here.
                    services.AddTransient<IModelComparer, SchemaPresenceComparer>();
                    services.AddTransient<IModelComparer, IndexComparer>();
                    services.AddTransient<IModelComparer, ViewComparer>();
                    services.AddTransient<IModelComparer, FunctionComparer>();
                    services.AddTransient<IModelComparer, SequenceComparer>();
                    services.AddTransient<IModelComparer, TriggerComparer>();
                    services.AddTransient<IModelComparer, TypeComparer>();

                    services.AddSingleton<ISettingsService>(settingsService);
                    services.AddReportGenerators();
                    services.AddSingleton<IReportExportService, ReportExportService>();
                    services.AddSingleton<IUpdateService, UpdateService>();
                    services.AddTransient<UnifiedConnectionManagerViewModel>();
                    services.AddTransient<MainViewModel>();
                })
            .Build();
    }
}
