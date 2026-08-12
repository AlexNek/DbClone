using System;

namespace DbClone.UI.Services;

/// <summary>Event args raised during the update install process.</summary>
public sealed class InstallProgressEventArgs : EventArgs
{
    public InstallProgressEventArgs(InstallProgressState state, string? errorMessage = null)
    {
        State = state;
        ErrorMessage = errorMessage;
    }

    public InstallProgressState State { get; }

    public string? ErrorMessage { get; }
}
