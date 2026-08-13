using System;

namespace DbClone.UI.Services;

/// <summary>Event args raised during the update install process.</summary>
public sealed class InstallProgressEventArgs : EventArgs
{
    public InstallProgressEventArgs(InstallProgressState state, string? errorMessage = null, int progressPercent = 0)
    {
        State = state;
        ErrorMessage = errorMessage;
        ProgressPercent = progressPercent;
    }

    public InstallProgressState State { get; }

    public string? ErrorMessage { get; }

    /// <summary>Download progress 0–100 (only meaningful for <see cref="InstallProgressState.DownloadProgress"/>).</summary>
    public int ProgressPercent { get; }
}
