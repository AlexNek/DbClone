using CanonicalModel.Interchange;

namespace DatabaseInterchange.Script;

/// <summary>
/// One table's block as the plan places it: the table, and which part of it the block writes (D1, FR5).
/// </summary>
/// <remarks>
/// <see cref="Part"/> is one for a table the plan keeps whole — including every table a file packs beside
/// others — and counts from one for a table larger than the budget. The writer numbers the parts it actually
/// writes, so a spill the estimate did not predict still produces a complete block.
/// </remarks>
/// <param name="Table">The table whose rows the block writes.</param>
/// <param name="Part">Which part of the table the block is, counting from one.</param>
/// <param name="Rows">How many rows the plan expects the block to carry.</param>
internal sealed record ScriptChunk(TableDefinition Table, int Part, long Rows);
