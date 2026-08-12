namespace DbClone.UI.Services;

/// <summary>States reported during the update install process.</summary>
public enum InstallProgressState
{
    Downloading,
    DownloadProgress,
    Launching,
    Failed
}
