using CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Pipeline;

/// <summary>
/// The foreign keys that are not created because their parent table is outside the copied
/// scope, counted once per parent so a scope consequence is one line rather than one line
/// per constraint.
/// </summary>
/// <param name="Parent">The referenced table every constraint of this group points at.</param>
/// <param name="Count">The number of constraints deferred to that parent.</param>
public sealed record DeferredForeignKeyGroup(TableId Parent, int Count);
