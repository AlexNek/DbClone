using DbClone.Application.DTOs;
using DbClone.Application.Platforms;

namespace DbClone.Application.Copy;

/// <summary>
/// Context shared across all pipeline stages.
/// </summary>
public sealed class CopyContext
{
    /// <summary>
    /// Gets or sets the connection heartbeat action.
    /// Called between pipeline stages to keep connections alive and detect dead TCP connections.
    /// Set by the database provider (e.g. PostgreSQL sends SELECT 1 on both connections).
    /// </summary>
    public Func<CopyContext, CancellationToken, Task>? ConnectionHeartbeat { get; set; }

    /// <summary>
    /// Gets or sets the dependency analysis result.
    /// </summary>
    public DependencyResult? DependencyResult { get; set; }

    /// <summary>
    /// Gets or sets the detected destination capabilities.
    /// </summary>
    public ServerCapabilities? DestinationCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the destination database connection (provider-specific).
    /// </summary>
    public object? DestinationConnection { get; set; }

    /// <summary>
    /// Gets or sets the destination connection string (for reopening connections).
    /// </summary>
    public string? DestinationConnectionString { get; set; }

    /// <summary>
    /// Gets the list of errors accumulated during the pipeline.
    /// </summary>
    public List<CopyError> Errors { get; } = [];

    /// <summary>
    /// Gets the schemas that are excluded from the copy because the current user
    /// lacks CREATE privilege on them at the destination. Objects in these schemas
    /// are removed from the working model and reported once (per schema) as warnings.
    /// </summary>
    public HashSet<string> ExcludedSchemas { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the list of table names that had row count mismatches during validation.
    /// Used by the re-copy stage to selectively retry failed tables.
    /// </summary>
    public List<string> MismatchedTables { get; } = [];

    /// <summary>
    /// Gets or sets the progress reporter for the pipeline.
    /// </summary>
    public IProgress<CopyProgress>? Progress { get; set; }

    /// <summary>
    /// Gets or sets a named property for stage communication.
    /// </summary>
    public Dictionary<string, object> Properties { get; } = [];

    /// <summary>
    /// Gets the copy request.
    /// </summary>
    public required CopyRequest Request { get; init; }

    /// <summary>
    /// Gets the extensions that could not be created on the destination.
    /// Key = extension name, Value = schema the extension is installed in on the source.
    /// Downstream stages use this to attribute dependent object failures to the
    /// unavailable extension and report them as notifications instead of fatal errors.
    /// </summary>
    public Dictionary<string, string> SkippedExtensions { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the qualified names of tables that could not be created on the destination
    /// because they depend on a skipped extension. These tables are excluded from
    /// data copy and validation, and reported as warnings for the user to review.
    /// </summary>
    public HashSet<string> SkippedTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the detected source capabilities.
    /// </summary>
    public ServerCapabilities? SourceCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the source database connection (provider-specific).
    /// </summary>
    public object? SourceConnection { get; set; }

    /// <summary>
    /// Gets or sets the source connection string (for reopening connections).
    /// </summary>
    public string? SourceConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the source database model.
    /// </summary>
    public Models.DatabaseModel? SourceModel { get; set; }

    /// <summary>
    /// Gets or sets the resolved platform definition for the source connection.
    /// Populated by ConnectStage after successful connection.
    /// </summary>
    public PlatformResolution? SourcePlatformResolution { get; set; }

    /// <summary>
    /// Gets or sets the resolved platform definition for the destination connection.
    /// Populated by ConnectStage after successful connection.
    /// </summary>
    public PlatformResolution? DestinationPlatformResolution { get; set; }

    /// <summary>
    /// Gets the list of stage results.
    /// </summary>
    public List<StageResult> StageResults { get; } = [];

    /// <summary>
    /// Gets or sets the copy statistics.
    /// </summary>
    public CopyStatistics Statistics { get; set; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Gets or sets the total number of stages in the pipeline.
    /// </summary>
    public int TotalStages { get; set; }

    /// <summary>
    /// Gets or sets the elapsed time stopwatch for the pipeline.
    /// </summary>
    public System.Diagnostics.Stopwatch? TotalStopwatch { get; set; }

    /// <summary>
    /// Gets the list of warnings accumulated during the pipeline.
    /// </summary>
    public List<CopyWarning> Warnings { get; } = [];
}
