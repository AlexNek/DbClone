using DbAdapter.Definitions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DatabaseInterchange.Transform;

/// <summary>
/// The runtime for database interchange operations.
/// Constructed with an adapter; provides exporter, importer, and planner.
/// </summary>
public sealed class DatabaseInterchangeRuntime
{
    private readonly IDatabaseAdapter _adapter;
    private readonly ILoggerFactory _loggerFactory;

    private DatabaseInterchangeRuntime(IDatabaseAdapter adapter, ILoggerFactory loggerFactory)
    {
        _adapter = adapter;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Creates a new interchange runtime for the specified adapter.
    /// </summary>
    /// <param name="adapter">The database adapter.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <returns>A new runtime instance.</returns>
    public static DatabaseInterchangeRuntime Create(
        IDatabaseAdapter adapter,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return new DatabaseInterchangeRuntime(adapter, loggerFactory ?? NullLoggerFactory.Instance);
    }

    /// <summary>
    /// Creates a snapshot exporter.
    /// </summary>
    /// <returns>A new exporter instance.</returns>
    public SnapshotExporter CreateExporter() =>
        new(_adapter, _loggerFactory.CreateLogger<SnapshotExporter>());

    /// <summary>
    /// Creates a snapshot importer.
    /// </summary>
    /// <returns>A new importer instance.</returns>
    public SnapshotImporter CreateImporter() =>
        new(_adapter, _loggerFactory.CreateLogger<SnapshotImporter>());

    /// <summary>
    /// Creates an import planner.
    /// </summary>
    /// <returns>A new planner instance.</returns>
    public ImportPlanner CreatePlanner() =>
        new(_adapter, _loggerFactory.CreateLogger<ImportPlanner>());
}
