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
    /// Extracts binary values from a catalog table to individual files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The extractor reads chunk files and blob containers, writing each binary value to a separate
    /// file named <c>&lt;table&gt;_&lt;row-index&gt;.&lt;suffix&gt;</c>. The suffix comes from the
    /// detected format in the blob container name (for references) or from the magic bytes (for inline values).
    /// </para>
    /// </remarks>
    /// <param name="request">The extraction request.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The extraction result.</returns>
    Task<BlobExtractionResult> ExtractBlobsAsync(
        BlobExtractionRequest request,
        IProgress<BlobExtractionProgressView>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Scans the catalog for all tables with binary columns and extracts their blobs.
    /// </summary>
    /// <param name="catalogPath">Path to the catalog root directory.</param>
    /// <param name="outputDirectory">Directory where extracted files will be written.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The aggregated extraction result.</returns>
    Task<BlobExtractionResult> ExtractAllBlobsAsync(
        string catalogPath,
        string outputDirectory,
        IProgress<BlobExtractionProgressView>? progress,
        CancellationToken cancellationToken);
}
