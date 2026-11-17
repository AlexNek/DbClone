using CanonicalModel.Interchange;
using DbAdapter.Definitions;

namespace DatabaseInterchange.Transform;

/// <summary>
/// One step of the order a catalog is applied in: the stage, the objects handed to it, and the progress
/// percentage a restore reports when it reaches it.
/// </summary>
/// <remarks>
/// A restore and a script must not diverge in the order they create objects, so both walk the list
/// <see cref="SchemaStagePlan"/> builds from these records rather than each stating the sequence itself.
/// </remarks>
internal sealed record SchemaStageStep
{
    /// <summary>Gets the stage this step applies.</summary>
    public required SchemaStage Stage { get; init; }

    /// <summary>Gets the objects handed to the stage, in the order their section records them.</summary>
    public required IReadOnlyList<ISchemaObject> Objects { get; init; }

    /// <summary>Gets the progress percentage a restore reports when it reaches this step.</summary>
    public int Percentage { get; init; }
}
