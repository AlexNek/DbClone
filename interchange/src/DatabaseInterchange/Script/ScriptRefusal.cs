namespace DatabaseInterchange.Script;

/// <summary>
/// Why a script pass wrote nothing, stated once so the issue list and the summary say the same thing.
/// </summary>
/// <remarks>
/// <para>
/// A refusal is not a failure of the store: the catalog on disk is complete and restorable whether or not a
/// script could be produced (FR8). Every refusal is therefore one <c>Warning</c> issue and one sentence the
/// report repeats, never an exception that would take the store down with it.
/// </para>
/// <para>
/// The pass refuses rather than writing what it can: a schema file missing the types a table needs, or
/// missing one table, is worse than no file at all — it looks runnable and is not.
/// </para>
/// </remarks>
public sealed record ScriptRefusal
{
    /// <summary>Gets the stable issue code the refusal is recorded under.</summary>
    public required string Code { get; init; }

    /// <summary>Gets the reason, naming the object when one is to blame.</summary>
    public required string Reason { get; init; }
}
