using DbClone.Application.DTOs;
using DbClone.Application.TableFilter;

namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Request for the store command.
/// </summary>
public sealed record StoreRequest(
    ConnectionInfo Connection,
    string CatalogPath,
    int ChunkBytes,
    int RowsPerChunk,
    bool IncludeData,
    string SnapshotConsistency,
    string UnsupportedFeaturePolicy,
    bool PackAfterStore,
    TableSelectionSpec? TableSelection);
