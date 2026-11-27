namespace DbAdapter.Definitions;

/// <summary>
/// What the caller intends to do with the database model — permission for an adapter to skip
/// what that use cannot see, never a demand. An adapter may always answer a scoped request with
/// more; answering an <see cref="Everything"/> request with less is a contract violation.
/// The default is the safe one.
/// </summary>
public enum EMetadataScope
{
    /// <summary>
    /// Every object the adapter can read — the whole model a copy, a comparison, a store or a
    /// restore needs.
    /// </summary>
    Everything,

    /// <summary>
    /// What a picture of the schema is drawn from: schemas, tables, columns, primary and unique
    /// keys, foreign keys, check constraints and indexes. Views, materialized views, routines,
    /// triggers, policies, publications, subscriptions, sequences, enums, domains and composite
    /// types are not promised.
    /// </summary>
    Structure,
}
