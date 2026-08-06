using DbClone.Application.DTOs;
using DbClone.UI.ViewModels;

namespace DbClone.UI.Services;

/// <summary>
/// Result of a copy workflow execution.
/// </summary>
public sealed class CopyWorkflowResult
{
    public string? BackupDatabaseName { get; set; }

    public ConnectionViewModel? BackupDestination { get; set; }

    public string? ErrorMessage { get; set; }

    public Exception? Exception { get; set; }

    public CopyResult? Result { get; set; }

    public bool Success { get; set; }
}
