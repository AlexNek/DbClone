using CanonicalModel.Interchange;

namespace DbAdapter.Definitions;

/// <summary>
/// Renders a catalog's content as the engine's own SQL that the server accepts more than once.
/// Text only: this role opens no file, no connection and no transaction.
/// </summary>
/// <remarks>
/// The statements are the text the restore already produces, guarded so a second run creates no object
/// (the store writes them out; the restore executes them). The product owns the catalog layout, the file
/// names, the ordering and — since feature 10 — where a table's block is cut, because only it knows how full
/// the file it is filling has become. This role owns every word of the SQL, because only the adapter speaks
/// the engine's dialect: the block is opened, filled and closed through three calls, and the adapter writes
/// all four of those parts.
/// </remarks>
public interface ISqlScriptRenderer
{
    /// <summary>Gets the dialect the text it answers with is written in.</summary>
    string Dialect { get; }

    /// <summary>
    /// The statements a file's header carries: the two settings every literal in the file is read under.
    /// They are session-level, so a reader that runs the file statement by statement keeps them.
    /// </summary>
    IReadOnlyList<string> HeaderStatements();

    /// <summary>
    /// One stage's statements, each re-runnable: the text the restore produces, guarded so the statement
    /// can be executed against a database that already holds what the same file created.
    /// </summary>
    /// <param name="stage">The stage whose objects are handed over.</param>
    /// <param name="objects">The objects the catalog recorded for that stage.</param>
    /// <returns>The stage's statements, in the order they must run.</returns>
    IReadOnlyList<string> SchemaStatements(SchemaStage stage, IReadOnlyList<ISchemaObject> objects);

    /// <summary>
    /// Opens one table's block: the banner naming the table, its recorded row count and — when the product
    /// split it — the part, then the transaction and the settings its literals assume.
    /// </summary>
    /// <remarks>
    /// The product calls this again in the next file when a table's rows outgrow the file being written, so
    /// one table may be several blocks; each is a transaction of its own (D4).
    /// </remarks>
    /// <param name="table">The table the block writes.</param>
    /// <param name="rowCount">How many rows the catalog records for the table, for the banner.</param>
    /// <param name="part">The part this block is, or <see cref="ScriptBlockPart.Whole"/> for an unsplit table.</param>
    /// <returns>The statements that open the block, in the order they must run.</returns>
    IReadOnlyList<string> BeginTableBlock(TableDefinition table, long rowCount, ScriptBlockPart part);

    /// <summary>
    /// One table's rows as re-runnable <c>INSERT</c> statements — the block's contents only, with no
    /// transaction of their own.
    /// </summary>
    /// <remarks>
    /// Both directions are streams. The rows arrive as the catalog reads them and the statements leave as
    /// they are built, so a table larger than memory is written without ever being held whole: the
    /// renderer keeps the one batch it is building — the unit the restore's own data pass uses — and the
    /// product writes each statement as it arrives.
    /// </remarks>
    /// <param name="table">The table the rows belong to.</param>
    /// <param name="rows">The rows, in the order the catalog stored them.</param>
    /// <param name="ct">Cancellation token, honoured between rows.</param>
    /// <returns>
    /// Each statement with the number of rows it carries, which is what lets the product state a file's row
    /// count exactly rather than estimate it (D9).
    /// </returns>
    IAsyncEnumerable<(string Statement, int Rows)> InsertStatements(
        TableDefinition table,
        IAsyncEnumerable<DataValue[]> rows,
        CancellationToken ct = default);

    /// <summary>Closes one table's block: the commit that makes what was inserted there true (D4, FR5).</summary>
    /// <returns>The statements that close the block, in the order they must run.</returns>
    IReadOnlyList<string> EndTableBlock();

    /// <summary>
    /// The statements that make the schema true after the data — key validation and materialized-view
    /// refresh. No sequence state travels here: a catalog records none.
    /// </summary>
    /// <param name="tables">The catalog's tables, whose foreign keys the tail validates.</param>
    /// <param name="views">The catalog's views, of which the materialized ones are refreshed.</param>
    /// <returns>The tail's statements, in the order they must run.</returns>
    IReadOnlyList<string> TailStatements(
        IReadOnlyList<TableDefinition> tables,
        IReadOnlyList<ViewDefinition> views);
}
