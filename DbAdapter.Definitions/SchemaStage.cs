namespace DbAdapter.Definitions;

/// <summary>
/// Schema import/export stages in dependency order.
/// </summary>
public enum SchemaStage
{
    /// <summary>Database and schemas.</summary>
    Schemas = 10,

    /// <summary>
    /// Extensions, created once the schemas they are installed into exist and before anything that
    /// names an object they provide — an operator class in an index, a type in a column.
    /// </summary>
    Extensions = 15,

    /// <summary>Types (domains, enums, composites, ranges, collations, operators, casts).</summary>
    Types = 20,

    /// <summary>Sequences (definitions only; current value comes later).</summary>
    Sequences = 30,

    /// <summary>Tables, columns, partitions, native properties.</summary>
    Tables = 40,

    /// <summary>Functions, procedures, aggregates, window functions.</summary>
    Routines = 50,

    /// <summary>Views and materialized views.</summary>
    Views = 60,

    /// <summary>Primary keys, unique and check constraints; foreign keys where the plan puts them.</summary>
    Constraints = 70,

    /// <summary>Indexes and exclusion constraints.</summary>
    Indexes = 80,

    /// <summary>Triggers and event triggers.</summary>
    Triggers = 90,

    /// <summary>Publications, created after the tables they enumerate.</summary>
    Publications = 95,

    /// <summary>Subscriptions, created after the publications they read.</summary>
    Subscriptions = 96,

    /// <summary>Sequence state, deferred foreign keys, post-import state.</summary>
    PostData = 100,
}

/// <summary>
/// Security import stages in execution order.
/// </summary>
public enum ESecurityStage
{
    /// <summary>Roles and memberships.</summary>
    Principals = 1,

    /// <summary>Object ownership.</summary>
    Ownership = 2,

    /// <summary>Row-level security enable and policies.</summary>
    RowLevelSecurity = 3,

    /// <summary>Grants and default privileges.</summary>
    Grants = 4,
}
