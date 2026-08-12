using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;

using AutoUpdaterDotNET;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DbClone.UI.Services;

/// <summary>Result of an update check.</summary>
public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    Version? Version,
    string? DownloadUrl,
    string? ChangelogUrl);

/// <summary>Event args raised when an update check completes.</summary>
public sealed class UpdateCheckCompletedEventArgs : EventArgs
{
    public UpdateCheckCompletedEventArgs(UpdateCheckResult result, bool reportErrors, bool isError = false)
    {
        Result = result;
        ReportErrors = reportErrors;
        IsError = isError;
    }

    public UpdateCheckResult Result { get; }

    /// <summary>True when the check was triggered by an explicit user action.</summary>
    public bool ReportErrors { get; }

    /// <summary>True when the check failed (network error, parse failure, etc.).</summary>
    public bool IsError { get; }
}

/// <summary>
/// Provides application auto-update via GitHub Releases using AutoUpdater.NET.
/// </summary>
public interface IUpdateService
{
    /// <summary>Latest completed check result, or null if no check has run yet.</summary>
    UpdateCheckResult? LastCheck { get; }

    /// <summary>Raised on the UI thread whenever a check completes (available or not).</summary>
    event EventHandler<UpdateCheckCompletedEventArgs>? UpdateCheckCompleted;

    /// <summary>
    /// Checks for updates without showing the built-in modal dialog. Results are
    /// surfaced through <see cref="UpdateCheckCompleted"/>. If
    /// <paramref name="reportErrors"/> is true (manual check), failures are reported.
    /// </summary>
    void CheckForUpdates(bool reportErrors = false);

    /// <summary>Downloads and installs the available update.</summary>
    void InstallUpdate();

    /// <summary>Raised to report download/install progress and errors.</summary>
    event EventHandler<InstallProgressEventArgs>? InstallProgressChanged;
}

public sealed class UpdateService : IUpdateService
{
    private const string DefaultUrl =
        "https://api.github.com/repos/AlexNek/DbClone/releases/latest";

    /// <summary>
    /// Silent switches for the WiX Burn bundle installer (replaces the legacy
    /// Inno Setup /VERYSILENT /SUPPRESSMSGBOXES /NORESTART flags).
    /// </summary>
    private const string InstallerArguments = "/passive /norestart /relaunch";

    private readonly ILogger<UpdateService> _logger;

    private readonly string _updateUrl;

    private UpdateInfoEventArgs? _lastArgs;

    private bool _reportErrors;

    public UpdateService(ILogger<UpdateService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _updateUrl = configuration["Update:Url"] ?? DefaultUrl;

        // Subscribe once for the service lifetime. AutoUpdater.Start() runs the
        // check on a background thread, so unsubscribing right after Start()
        // would drop the handler before the feed is parsed (falling back to the
        // default XML parser which cannot handle GitHub's JSON release feeds).
        AutoUpdater.ParseUpdateInfoEvent += OnParseUpdateInfo;

        // Handle the result ourselves instead of letting AutoUpdater.NET show its
        // built-in modal dialog — we surface a non-blocking banner instead.
        AutoUpdater.CheckForUpdateEvent += OnCheckForUpdate;
    }

    public UpdateCheckResult? LastCheck { get; private set; }

    public event EventHandler<UpdateCheckCompletedEventArgs>? UpdateCheckCompleted;

    public event EventHandler<InstallProgressEventArgs>? InstallProgressChanged;

    public void CheckForUpdates(bool reportErrors = false)
    {
        _logger.LogInformation(
            "Checking for updates (reportErrors={ReportErrors}, url={Url})",
            reportErrors,
            _updateUrl);

        _reportErrors = reportErrors;

        AutoUpdater.InstalledVersion = GetInstalledVersion();
        AutoUpdater.HttpUserAgent = "DbClone-AutoUpdater";
        AutoUpdater.ReportErrors = reportErrors;
        AutoUpdater.ShowSkipButton = false;
        AutoUpdater.ShowRemindLaterButton = false;
        AutoUpdater.RunUpdateAsAdmin = false;

        try
        {
            AutoUpdater.Start(_updateUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
        }
    }

    public void InstallUpdate()
    {
        if (_lastArgs is null)
        {
            _logger.LogWarning("InstallUpdate called but no update is available");
            return;
        }

        InstallProgressChanged?.Invoke(
            this,
            new InstallProgressEventArgs(InstallProgressState.Downloading));

        // Fire-and-forget: the download must not block the UI thread.
        _ = InstallUpdateAsync(_lastArgs);
    }

    private async Task InstallUpdateAsync(UpdateInfoEventArgs args)
    {
        try
        {
            // Download ourselves instead of AutoUpdater.DownloadUpdate, which
            // launches .exe installers without arguments (interactive setup).
            var installerPath = await DownloadInstallerAsync(args.DownloadURL);

            InstallProgressChanged?.Invoke(
                this,
                new InstallProgressEventArgs(InstallProgressState.Launching));

            _logger.LogInformation(
                "Launching installer {InstallerPath} with arguments '{Arguments}'",
                installerPath,
                InstallerArguments);

            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = InstallerArguments,
                UseShellExecute = true
            });

            // The installer has started — close the app so files can be replaced.
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => System.Windows.Application.Current.Shutdown());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update install failed");

            var userMessage = ex switch
            {
                HttpRequestException => "Download failed. Check your internet connection and try again.",
                IOException io when io.Message.Contains("access", StringComparison.OrdinalIgnoreCase)
                    => "File access denied — your antivirus may be blocking the installer. " +
                       "Try disabling it temporarily or download the update manually from GitHub.",
                IOException => "Could not save the installer file. Check disk space and try again.",
                System.ComponentModel.Win32Exception => "Could not launch the installer — it may have been " +
                       "blocked by your antivirus. Try downloading manually from GitHub.",
                _ => $"Update failed: {ex.Message}"
            };

            InstallProgressChanged?.Invoke(
                this,
                new InstallProgressEventArgs(InstallProgressState.Failed, userMessage));
        }
    }

    private static async Task<string> DownloadInstallerAsync(string url)
    {
        var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "DbClone-Setup.exe";

        var target = Path.Combine(Path.GetTempPath(), fileName);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DbClone-AutoUpdater");

        using var stream = await http.GetStreamAsync(url);
        await using var file = File.Create(target);
        await stream.CopyToAsync(file);

        return target;
    }

    private void OnCheckForUpdate(UpdateInfoEventArgs args)
    {
        if (args.Error is not null)
        {
            _logger.LogWarning(args.Error, "Update check failed");
            LastCheck = new UpdateCheckResult(false, null, null, null);
            UpdateCheckCompleted?.Invoke(
                this,
                new UpdateCheckCompletedEventArgs(LastCheck, _reportErrors, isError: true));
            return;
        }

        if (args.IsUpdateAvailable)
        {
            _lastArgs = args;
            var version = Version.TryParse(args.CurrentVersion, out var v) ? v : null;
            LastCheck = new UpdateCheckResult(true, version, args.DownloadURL, args.ChangelogURL);
        }
        else
        {
            _lastArgs = null;
            LastCheck = new UpdateCheckResult(false, null, null, null);
        }

        _logger.LogInformation(
            "Update check completed. Update available: {Available}",
            LastCheck.IsUpdateAvailable);

        UpdateCheckCompleted?.Invoke(
            this,
            new UpdateCheckCompletedEventArgs(LastCheck, _reportErrors));
    }

    /// <summary>
    /// Gets the installed version from GitVersion information.
    /// Falls back to assembly version if GitVersion data is unavailable.
    /// </summary>
    private static Version GetInstalledVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var gvType = assembly.GetType("GitVersionInformation");
        var majorMinorPatch = gvType?.GetField("MajorMinorPatch")?.GetValue(null) as string;

        if (majorMinorPatch is not null && Version.TryParse(majorMinorPatch, out var version))
            return version;

        return assembly.GetName().Version ?? new Version(1, 0, 0);
    }

    private void OnParseUpdateInfo(ParseUpdateInfoEventArgs args)
    {
        try
        {
            var data = args.RemoteData;
            if (string.IsNullOrWhiteSpace(data))
            {
                _logger.LogWarning("Update feed is empty");
                return;
            }

            if (data.TrimStart().StartsWith("<", StringComparison.Ordinal))
                ParseXmlUpdateInfo(args, data);
            else
                ParseJsonUpdateInfo(args, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse update feed");
        }
    }

    /// <summary>
    /// Parses an AutoUpdater.NET-style XML feed (version/url/changelog).
    /// </summary>
    private void ParseXmlUpdateInfo(ParseUpdateInfoEventArgs args, string data)
    {
        var doc = new XmlDocument();
        doc.LoadXml(data);

        var versionText = doc.SelectSingleNode("//version")?.InnerText ?? "";
        var downloadUrl = doc.SelectSingleNode("//url")?.InnerText ?? "";
        var changelogUrl = doc.SelectSingleNode("//changelog")?.InnerText;

        var version = ParseVersion(versionText);
        if (version is null)
        {
            _logger.LogWarning("Could not parse version from XML feed '{Version}'", versionText);
            return;
        }

        if (string.IsNullOrEmpty(downloadUrl))
        {
            _logger.LogWarning("No download URL in XML update feed");
            return;
        }

        args.UpdateInfo = new UpdateInfoEventArgs
                              {
                                  CurrentVersion = version.ToString(),
                                  DownloadURL = downloadUrl,
                                  ChangelogURL = changelogUrl
                              };

        _logger.LogInformation(
            "Update info parsed (XML): version={Version}, url={Url}",
            version,
            downloadUrl);
    }

    /// <summary>
    /// Parses a GitHub Releases API JSON feed (tag_name/assets/html_url).
    /// </summary>
    private void ParseJsonUpdateInfo(ParseUpdateInfoEventArgs args, string data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tagName);
        if (version is null)
        {
            _logger.LogWarning("Could not parse version from tag '{Tag}'", tagName);
            return;
        }

        // Find the installer asset (.exe) from the release. Releases may carry
        // one installer per platform (same source, different RID); prefer the
        // asset matching this process architecture. The default win-x64 build
        // keeps the unsuffixed name, so a plain .exe is the fallback.
        var downloadUrl = "";
        var changelogUrl = root.GetProperty("html_url").GetString() ?? "";
        var platformSuffix = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "-win-arm64",
            System.Runtime.InteropServices.Architecture.X86 => "-win-x86",
            _ => "-win-x64"
        };

        if (root.TryGetProperty("assets", out var assets))
        {
            string? matchingUrl = null;
            string? firstExeUrl = null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (name.Contains(platformSuffix, StringComparison.OrdinalIgnoreCase))
                    matchingUrl = url;
                firstExeUrl ??= url;
            }

            downloadUrl = matchingUrl ?? firstExeUrl ?? "";
        }

        if (string.IsNullOrEmpty(downloadUrl))
        {
            _logger.LogWarning("No installer asset found in release {Tag}", tagName);
            return;
        }

        args.UpdateInfo = new UpdateInfoEventArgs
                              {
                                  CurrentVersion = version.ToString(),
                                  DownloadURL = downloadUrl,
                                  ChangelogURL = changelogUrl
                              };

        _logger.LogInformation(
            "Update info parsed (JSON): version={Version}, url={Url}",
            version,
            downloadUrl);
    }

    /// <summary>
    /// Parses a version from a git tag like "v1.2.3" or "1.2.3".
    /// </summary>
    private static Version? ParseVersion(string tag)
    {
        var cleaned = tag.TrimStart('v', 'V');
        return Version.TryParse(cleaned, out var version) ? version : null;
    }
}
