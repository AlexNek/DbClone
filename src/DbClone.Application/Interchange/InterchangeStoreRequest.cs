using DbClone.Application.DTOs;
using DbClone.Application.TableFilter;

namespace DbClone.Application.Interchange;

/// <summary>
/// Application-level request to store (export) a database into a catalog.
/// The wrapper translates this into the product's own request type.
/// </summary>
/// <remarks>
/// <c>IncludeSummaryReport</c> is a user preference held here in the app and translated into the
/// product's own option name by the wrapper — the app never learns the catalog's file names.
/// <c>IncludeSqlScripts</c> follows the same rule and defaults to <see langword="false"/> at this
/// boundary: the pass is opt-in at the product edge, and it is the application preference that makes it
/// on for a user, so a caller that builds a request without thinking about it gets today's catalog.
/// </remarks>
public sealed record InterchangeStoreRequest(
    ConnectionInfo Connection,
    TableSelectionSpec TableSelection,
    string CatalogPath,
    int ChunkBytes,
    int RowsPerChunk,
    bool IncludeData,
    string SnapshotConsistency,
    string UnsupportedFeaturePolicy,
    bool VerifyAfterStore,
    bool IncludeSummaryReport = true,
    bool IncludeSqlScripts = false);
