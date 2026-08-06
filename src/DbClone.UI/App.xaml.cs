using System.IO;
using System.Windows;

using DbClone.Application.Compare;
using DbClone.Application.Compare.Comparers;
using DbClone.Application.Enums;
using DbClone.PostgreSql;
using DbClone.UI.Logging;
using DbClone.UI.Services;
using DbClone.UI.ViewModels;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Serilog;

using Wpf.Ui.Appearance;

namespace DbClone.UI;

public partial class App : System.Windows.Application, IDisposable
{
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

            _host = BuildHost();

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
            var crashDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppFolder.Name);
            Directory.CreateDirectory(crashDir);
            var crashLog = Path.Combine(crashDir, "crash.log");
            File.WriteAllText(crashLog, $"CRASH at {DateTime.Now:O}\n{ex}");

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

    private static IHost BuildHost()
    {
        return Host.CreateDefaultBuilder()
            .UseEnvironment(
#if DEBUG
                "Development"
#else
                "Production"
#endif
            )
            .ConfigureAppConfiguration((context, config) =>
                {
                    config.SetBasePath(AppContext.BaseDirectory);
                    config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                })
            .UseSerilog(Log.Logger)
            .ConfigureServices((_, services) =>
                {
                    services.AddPostgreSqlProvider();
                    services.AddSingleton<IDialogService, WpfDialogService>();
                    services.AddSingleton<IConnectionStore, ConnectionStore>();
                    services.AddSingleton<IConnectionGroupStore, ConnectionGroupStore>();
                    services.AddSingleton<IBackupEncryptionService, BackupEncryptionService>();
                    services.AddTransient<IDatabaseService, DatabaseService>();
                    services.AddTransient<IDatabaseComparerService, DatabaseComparerService>();

                    // Model comparers (IModelComparer) — OCP: add new ones here without editing DatabaseComparerService
                    services.AddTransient<IModelComparer, SchemaPresenceComparer>();
                    services.AddTransient<IModelComparer, IndexComparer>();
                    services.AddTransient<IModelComparer, ViewComparer>();
                    services.AddTransient<IModelComparer, FunctionComparer>();
                    services.AddTransient<IModelComparer, SequenceComparer>();
                    services.AddTransient<IModelComparer, TriggerComparer>();
                    services.AddTransient<IModelComparer, TypeComparer>();
                    services.AddTransient<IModelComparer, TableDdlComparer>();

                    services.AddTransient<CopyOperationOrchestrator>();
                    services.AddSingleton<ISettingsService, SettingsService>();
                    services.AddReportGenerators();
                    services.AddSingleton<ReportExportService>();
                    services.AddSingleton<IUpdateService, UpdateService>();
                    services.AddTransient<UnifiedConnectionManagerViewModel>();
                    services.AddTransient<MainViewModel>();
                })
            .Build();
    }
}
