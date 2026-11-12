namespace DatabaseInterchange.Script;

/// <summary>
/// One file of a planned data family: the blocks it holds, in the order they run, and what they are expected
/// to weigh (D3).
/// </summary>
/// <remarks>
/// The estimate is what the planner packed with. It is not a cap and it is not what the report states: the
/// writer fills the file until the bytes it actually writes no longer fit, and the report describes the files
/// that exist (D6).
/// </remarks>
internal sealed class ScriptPlanFile
{
    private readonly List<ScriptChunk> _chunks = [];

    /// <summary>Gets the blocks this file holds, in the order they run.</summary>
    internal IReadOnlyList<ScriptChunk> Chunks => _chunks;

    /// <summary>Gets how many bytes the blocks in this file are expected to take.</summary>
    internal long Estimate { get; private set; }

    /// <summary>Adds one block to the file.</summary>
    /// <param name="chunk">The block to add.</param>
    /// <param name="estimate">The bytes the block's table is expected to take.</param>
    internal void Add(ScriptChunk chunk, long estimate)
    {
        _chunks.Add(chunk);
        Estimate += estimate;
    }
}
