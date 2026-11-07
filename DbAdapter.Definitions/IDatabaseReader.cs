using CanonicalModel.Interchange;
using DbAdapter.Definitions.Interchange;

namespace DbAdapter.Definitions;

/// <summary>
/// The reader role: captures a database snapshot from the source.
/// Created per export operation; owns its connection and transaction.
/// </summary>
public interface IDatabaseReader : IAsyncDisposable
{
    /// <summary>Gets the adapter that created this reader.</summary>
    IDatabaseAdapter Adapter { get; }

    /// <summary>Gets the per-target capability detector over this reader's connection.</summary>
    ICapabilityDetector CapabilityDetector { get; }

    /// <summary>
    /// Reads the database schema (all objects, no data).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The schema snapshot.</returns>
    Task<SnapshotSchema> ReadSchemaAsync(CancellationToken ct);

    /// <summary>
    /// Reads rows from a table as an async stream.
    /// </summary>
    /// <param name="table">The table to read.</param>
    /// <param name="order">The requested row order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async stream of row batches.</returns>
    IAsyncEnumerable<DataValue[]> ReadTableRowsAsync(TableRef table, ERowOrder order, CancellationToken ct);

    /// <summary>
    /// Reads the objects that have no representation in the neutral model and must therefore be
    /// preserved as verbatim native SQL (custom types, domains, publications, subscriptions).
    /// Kinds that do have a structured home in <see cref="SnapshotSchema"/> are returned there with
    /// their native text attached, not repeated here. The catalog records this text together with
    /// the source dialect; whether it can be applied is decided at import, never here.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The native-only object definitions.</returns>
    Task<IReadOnlyList<NativeObjectDefinition>> ReadNativeObjectsAsync(CancellationToken ct);
}
