using CanonicalModel.DTOs;
using DbClone.Application.DTOs;
using DbClone.Application.Interfaces;
using DbClone.Application.TableFilter;

namespace DbClone.Application.Copy;

/// <summary>
/// The full copy workflow: verify connections, check permissions, optionally create
/// a backup database, optionally clean the destination, run the copy pipeline.
/// Lives in the application layer; the UI calls it through <see cref="ICopyWorkflow"/>.
/// </summary>
public interface ICopyWorkflow
{
    /// <summary>
    /// Executes the complete copy workflow.
    /// </summary>
    /// <param name="source">Source connection.</param>
    /// <param name="destination">Destination connection.</param>
    /// <param name="backupNamePrefix">
    /// The saved profile's BackupName, passed by the caller and resolved by the workflow
    /// with the chain: BackupName → source database name → source user name → "backup".
    /// </param>
    /// <param name="options">Copy options (mode, flags, etc.).</param>
    /// <param name="tableSelection">Optional table selection spec.</param>
    /// <param name="listener">Progress sink for log/phase/status messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The workflow result including any backup destination.</returns>
    Task<CopyWorkflowResult> ExecuteAsync(
        ConnectionInfo source,
        ConnectionInfo destination,
        string? backupNamePrefix,
        CopyOptions options,
        TableFilter.TableSelectionSpec? tableSelection,
        ICopyProgressSink listener,
        CancellationToken cancellationToken);
}
