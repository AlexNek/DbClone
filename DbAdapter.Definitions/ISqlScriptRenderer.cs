using CanonicalModel.Interchange;

namespace DbAdapter.Definitions;

/// <summary>
/// Renders a catalog's content as the engine's own SQL that the server accepts more than once.
/// Text only: this role opens no file, no connection and no transaction.
/// </summary>
/// <remarks>
/// The statements are the text the restore already produces, guarded so a second run creates no object
/// (the store writes them out; the restore executes them). The product owns the catalog layout, the file
/// names and the ordering; this role owns every word of the SQL, because only the adapter speaks the
/// engine's dialect.
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
    /// One table's rows as re-runnable <c>INSERT</c> statements, wrapped in the transaction and the
    /// settings the literals assume: skipping a row that is already there, never overwriting one.
    /// </summary>
    /// <remarks>
    /// Both directions are streams. The rows arrive as the catalog reads them and the statements leave as
    /// they are built, so a table larger than memory is written without ever being held whole: the
    /// renderer keeps the one batch it is building — the unit the restore's own data pass uses — and the
    /// product writes each statement as it arrives.
    /// </remarks>
    /// <param name="table">The table the rows belong to.</param>
    /// <param name="rows">The rows, in the order the catalog stored them.</param>
    /// <param name="rowCount">How many rows the catalog records for the table, for the block's banner.</param>
    /// <param name="ct">Cancellation token, honoured between rows.</param>
    /// <returns>The table's block: its banner, the transaction, the statements and the commit.</returns>
    IAsyncEnumerable<string> InsertStatements(
        TableDefinition table,
        IAsyncEnumerable<DataValue[]> rows,
        long rowCount,
        CancellationToken ct = default);

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
