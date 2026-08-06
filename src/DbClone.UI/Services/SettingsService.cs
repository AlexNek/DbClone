using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using DbClone.UI.Models;
using DbClone.UI.Settings;
using DbClone.UI.ViewModels;

using Serilog;

namespace DbClone.UI.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
                                                                    {
                                                                        WriteIndented = true,
                                                                        NumberHandling =
                                                                            JsonNumberHandling
                                                                                .AllowNamedFloatingPointLiterals,
                                                                        Converters =
                                                                            {
                                                                                new
                                                                                    JsonStringEnumConverter()
                                                                            }
                                                                    };

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolder.Name,
        "settings.json");

    public void ImportLegacy(
        IConnectionStore connectionStore,
        ConnectionViewModel source,
        ConnectionViewModel dest)
    {
        if (connectionStore.GetAll().Count > 0)
            return;

        var settings = Load();
        if (settings.Source != null)
        {
            var src = new SavedConnection
                          {
                              Name =
                                  $"Source — {settings.Source.Host}/{settings.Source.DatabaseName}",
                              Host = settings.Source.Host,
                              Port = settings.Source.Port,
                              DatabaseName = settings.Source.DatabaseName,
                              Username = settings.Source.Username,
                              Folder = "Local"
                          };
            connectionStore.Save(src);
            source.SelectedSavedConnection =
                source.SavedConnections.FirstOrDefault(c => c.Id == src.Id);
        }

        if (settings.Destination != null)
        {
            var dst = new SavedConnection
                          {
                              Name =
                                  $"Destination — {settings.Destination.Host}/{settings.Destination.DatabaseName}",
                              Host = settings.Destination.Host,
                              Port = settings.Destination.Port,
                              DatabaseName = settings.Destination.DatabaseName,
                              Username = settings.Destination.Username,
                              Folder = "Local"
                          };
            connectionStore.Save(dst);
            dest.SelectedSavedConnection =
                dest.SavedConnections.FirstOrDefault(c => c.Id == dst.Id);
        }
    }

    public UserSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Log.Debug(
                    "[SettingsService.Load] Reading from {Path}, json length={Len}",
                    SettingsPath,
                    json.Length);
                return JsonSerializer.Deserialize<UserSettings>(json, JsonOptions)
                       ?? new UserSettings();
            }

            Log.Debug(
                "[SettingsService.Load] File not found at {Path}, returning defaults",
                SettingsPath);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[SettingsService.Load] Failed to load settings from {Path}",
                SettingsPath);
        }

        return new UserSettings();
    }

    public void Save(UserSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
            Log.Debug("[SettingsService.Save] Written to {Path}", SettingsPath);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[SettingsService.Save] Failed to save settings to {Path}",
                SettingsPath);
        }
    }
}
