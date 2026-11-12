using DbClone.Application.DTOs;
using DbClone.Application.TableFilter;

namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Request for the store command.
/// </summary>
/// <remarks>
/// <c>IncludeSummaryReport</c> comes from the Store settings checkbox and decides whether the
/// catalog folder receives its summary file; it defaults to true so existing call sites keep
/// their meaning. <c>IncludeSqlScripts</c> is the same kind of preference and defaults to false, so a
/// caller that says nothing runs exactly the store this workflow ran before the scripts existed.
/// <c>SqlScriptsMaxFileMb</c> is the size the user picked and defaults to the preference's own 32 MB, so
/// the split is on for a caller that says nothing, the way the checkbox beside it is (D11).
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
    bool IncludeSummaryReport = true,
    bool IncludeSqlScripts = false,
    int SqlScriptsMaxFileMb = 32);
