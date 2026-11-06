namespace DbClone.Application.Interchange;

/// <summary>
/// Application-level facade for the interchange product.
/// Wraps export, import, inspection, verification, and packing behind DTOs the UI can consume
/// without naming any product type.
/// </summary>
public interface IInterchangeService
{
    /// <summary>
    /// Stores (exports) a database into a catalog directory.
    /// </summary>
    Task<InterchangeStoreResult> StoreAsync(
        InterchangeStoreRequest request,
        IProgress<InterchangeProgressView>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Builds the import plan for a catalog without opening a target connection.
    /// The plan shows stages, object counts, refused objects, and dependency cycles.
    /// </summary>
    Task<InterchangePlanView> PlanRestoreAsync(
        string catalogPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Restores (imports) a catalog into a database.
    /// The target must be empty; the product refuses non-empty targets.
    /// </summary>
    Task<InterchangeRestoreResult> RestoreAsync(
        InterchangeRestoreRequest request,
        IProgress<InterchangeProgressView>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inspects a catalog without a connection: format version, producer, export time,
    /// completeness, sections, tables, rows, chunks, preservation counts, and producer issues.
    /// </summary>
    Task<CatalogSummary> InspectAsync(
        string catalogPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Verifies a catalog's integrity (hashes and counts).
    /// A failure blocks restore and names the failing files.
    /// </summary>
    Task<VerificationSummary> VerifyAsync(
        string catalogPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Packs a catalog directory into a single <c>.rdbx.zip</c> file.
    /// Returns the path to the ZIP, or <c>null</c> when packing is not applicable.
    /// </summary>
    Task<string?> PackAsync(
        string catalogPath,
        CancellationToken cancellationToken);
}
