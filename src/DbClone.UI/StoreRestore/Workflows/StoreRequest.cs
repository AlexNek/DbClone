using DbClone.Application.DTOs;
using DbClone.Application.TableFilter;

namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Request for the store command.
/// </summary>
/// <remarks>
/// <c>IncludeSummaryReport</c> comes from the Store settings checkbox and decides whether the
/// catalog folder receives its summary file; it defaults to true so existing call sites keep
/// their meaning.
/// </remarks>
public sealed record StoreRequest(
    ConnectionInfo Connection,
    string CatalogPath,
    int ChunkBytes,
    int RowsPerChunk,
    bool IncludeData,
    string SnapshotConsistency,
    string UnsupportedFeaturePolicy,
    TableSelectionSpec? TableSelection,
    bool IncludeSummaryReport = true);
