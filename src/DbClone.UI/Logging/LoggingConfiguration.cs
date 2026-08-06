using System.IO;

using Microsoft.Extensions.Configuration;

using Serilog;

namespace DbClone.UI.Logging;

public static class LoggingConfiguration
{
    public static Serilog.Core.Logger CreateLogger()
    {
        var logDir = GetOrCreateLogDirectory();
        var logPath = logDir is not null
                          ? Path.Combine(logDir, "databasecopy-.log")
                          : null;

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        if (logPath is not null)
        {
            config["Serilog:WriteTo:0:Args:path"] = logPath;
        }

        return new LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .CreateLogger();
    }

    public static string? GetLogDirectory()
    {
        return GetOrCreateLogDirectory();
    }

    private static string? GetOrCreateLogDirectory()
    {
#if DEBUG
        // In debug, write logs next to the executable for easy access
        var debugDir = Path.Combine(AppContext.BaseDirectory, "logs");
        try
        {
            Directory.CreateDirectory(debugDir);
            return debugDir;
        }
        catch
        {
            return null;
        }
#else
        var appName = AppFolder.Name;
        var primary = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            appName, "logs");

        try
        {
            Directory.CreateDirectory(primary);
            return primary;
        }
        catch
        {
            var fallback = Path.Combine(Path.GetTempPath(), appName, "logs");
            try
            {
                Directory.CreateDirectory(fallback);
                return fallback;
            }
            catch
            {
                return null;
            }
        }
#endif
    }
}
