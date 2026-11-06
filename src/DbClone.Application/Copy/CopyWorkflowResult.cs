using DbClone.Application.DTOs;
using CanonicalModel.DTOs;

namespace DbClone.Application.Copy;

/// <summary>
/// Result of a copy workflow execution.
/// </summary>
public sealed class CopyWorkflowResult
{
    /// <summary>Name of the backup database created during backup mode.</summary>
    public string? BackupDatabaseName { get; set; }

    /// <summary>
    /// The connection the copy was ultimately pointed at — differs from the caller's
    /// destination when backup mode created a new database. Null when no backup was made.
    /// </summary>
    public ConnectionInfo? BackupDestination { get; set; }

    /// <summary>Human-readable error message when the workflow fails.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The exception that caused the failure, when applicable.</summary>
    public Exception? Exception { get; set; }

    /// <summary>The detailed copy result from the engine.</summary>
    public CopyResult? Result { get; set; }

    /// <summary>Whether the workflow completed successfully.</summary>
    public bool Success { get; set; }
}
