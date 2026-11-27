using CanonicalModel.Interchange;

namespace DbAdapter.Definitions.Interchange;

/// <summary>
/// Says which stage of a restore creates an object that arrived in the catalog's native section, and
/// the place each kind holds inside that stage.
/// <para>
/// The native file is written for what the structured sections have no home for, so one file carries
/// several kinds and no single stage creates them all. Handing the whole file to one stage and letting
/// that stage refuse the rest would put the routing decision inside whichever handler happened to
/// notice the mismatch, so adding a kind would mean editing a stage that has nothing to do with it.
/// This table is that decision stated once: an importer groups the file by <see cref="StageFor"/> and
/// each stage is then handed only the objects its own row claims.
/// </para>
/// <para>
/// Nothing here says whether a given engine creates such an object — that is
/// <see cref="IDatabaseCapabilities.CanCreate"/>, answered by the adapter for the target it was opened
/// for. An object is restored when the format has a stage that takes it from this file <b>and</b> the
/// adapter says it creates the kind; either answer missing is a refusal, and the refusal is made
/// before the first write rather than partway through the target.
/// </para>
/// </summary>
public static class NativeObjectRouting
{
    /// <summary>
    /// Names the stage that creates objects of this kind out of the native file, or
    /// <see langword="null"/> when no stage of the format takes that kind from that file.
    /// </summary>
    /// <remarks>
    /// A kind the structured sections already carry returns <see langword="null"/> — a table is created
    /// by the table stage from the table section, so the same object arriving through the native file
    /// would be a catalog holding two sources of truth for one object, which is worth refusing rather
    /// than resolving.
    /// </remarks>
    public static SchemaStage? StageFor(EObjectKind kind) =>
        kind switch
        {
            EObjectKind.Enum or
            EObjectKind.CompositeType or
            EObjectKind.RangeType or
            EObjectKind.Domain or
            EObjectKind.Collation or
            EObjectKind.Operator or
            EObjectKind.Cast => SchemaStage.Types,
            EObjectKind.Publication => SchemaStage.Publications,
            EObjectKind.Subscription => SchemaStage.Subscriptions,
            _ => null,
        };

    /// <summary>
    /// Places a kind in the order its stage creates the kinds of that stage in.
    /// </summary>
    /// <remarks>
    /// Kinds sharing a stage can depend on one another and the file gives no ids to follow, so the order
    /// is stated by kind: a composite can hold an enum's column and a domain can rest on either, so the
    /// types run enum, composite, range, domain, collation, operator, cast. Kinds whose stage carries no
    /// order among its objects share the same rank, which is what the default says.
    /// </remarks>
    public static int RankInStage(EObjectKind kind) =>
        kind switch
        {
            EObjectKind.Enum => 0,
            EObjectKind.CompositeType => 1,
            EObjectKind.RangeType => 2,
            EObjectKind.Domain => 3,
            EObjectKind.Collation => 4,
            EObjectKind.Operator => 5,
            EObjectKind.Cast => 6,
            _ => 0,
        };
}
