namespace DatabaseInterchange.Report;

/// <summary>
/// What one table's folders received, as stated by the writer while it wrote them (D3).
/// </summary>
/// <remarks>
/// <para>
/// This is a write-time fact, not a reading of the filesystem afterwards: the numbers are the ones
/// <see cref="Format.CatalogWriter"/> already had in hand per <c>WriteTableAsync</c> call, so the report
/// cannot disagree with the catalog it describes.
/// </para>
/// <para>
/// <paramref name="Chunks"/> and <paramref name="BlobFiles"/> count <em>files</em>, not non-empty files.
/// A table with no rows produces no chunk file at all (the row writer finalizes a chunk only once a row
/// has been written into it), so it reports <c>0</c> chunks and <c>0</c> row bytes rather than one empty
/// file. A blob container, by contrast, is written as its payload arrives, so an empty binary value
/// legitimately yields a 0-byte container: it counts here and contributes nothing to
/// <paramref name="BlobBytes"/>.
/// </para>
/// </remarks>
/// <param name="Schema">The schema the table was captured under.</param>
/// <param name="Table">The table name, as captured.</param>
/// <param name="Rows">Rows written for this table.</param>
/// <param name="Chunks">Row-chunk files written for this table.</param>
/// <param name="RowBytes">Total bytes of this table's chunk files — data, not metadata.</param>
/// <param name="BlobFiles">Blob container files written for this table.</param>
/// <param name="BlobBytes">Total bytes of this table's blob containers.</param>
internal sealed record TableWriteStats(
    string Schema,
    string Table,
    long Rows,
    int Chunks,
    long RowBytes,
    int BlobFiles,
    long BlobBytes)
{
    /// <summary>Gets the data bytes this table contributed: row chunks plus blob containers (FR3).</summary>
    public long DataBytes => RowBytes + BlobBytes;
}
