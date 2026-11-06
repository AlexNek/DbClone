using DbClone.Application.DTOs;
using DbClone.Application.TableFilter;

namespace DbClone.Application.Interchange;

/// <summary>
/// Application-level request to store (export) a database into a catalog.
/// The wrapper translates this into the product's own request type.
/// </summary>
public sealed record InterchangeStoreRequest(
    ConnectionInfo Connection,
    TableSelectionSpec TableSelection,
    string CatalogPath,
    int ChunkBytes,
    int RowsPerChunk,
    bool IncludeData,
    string SnapshotConsistency,
    string UnsupportedFeaturePolicy,
    bool PackAfterStore,
    bool VerifyAfterStore);
