using CanonicalModel.Interchange;
using DbAdapter.Definitions;

namespace DatabaseInterchange.Script;

/// <summary>
/// What one table of the catalog weighs, measured from the catalog's own records rather than from its rows
/// (FR3, D6).
/// </summary>
/// <remarks>
/// The estimate is deliberately not a promise: it is the number the layout is chosen with, and the writer
/// spills on the bytes it actually writes. The row factor errs low, on purpose, so a file closes late rather
/// than a family being planned too large — the two measured factors live in the plan (§2, D6).
/// </remarks>
/// <param name="Table">The table the measures belong to.</param>
/// <param name="Rows">The row count the catalog recorded for it.</param>
/// <param name="RowBytes">The bytes its <c>data.*.csv</c> chunks take on disk.</param>
/// <param name="PayloadBytes">The bytes its <c>blobs/</c> payloads take on disk, before they are inlined as base64.</param>
internal sealed record TableFootprint(
    TableDefinition Table,
    long Rows,
    long RowBytes,
    long PayloadBytes)
{
    /// <summary>The statement text a chunk's on-disk CSV byte produces, measured on a real catalog (2.23).</summary>
    private const double RowFactor = 2.0;

    /// <summary>What a payload byte costs once base64 and the <c>decode(…)</c> wrapper are written (D6).</summary>
    private const double PayloadFactor = 1.37;

    /// <summary>Gets how the catalog names the table.</summary>
    internal TableRef Ref => new(Table.SchemaRef.ObjectName() ?? string.Empty, Table.Name);

    /// <summary>Gets the bytes this table is expected to take in a data script file.</summary>
    internal long Estimate => (long)((RowBytes * RowFactor) + (PayloadBytes * PayloadFactor));
}
