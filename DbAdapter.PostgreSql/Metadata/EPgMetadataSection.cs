namespace DbAdapter.PostgreSql.Metadata;

/// <summary>
/// The sections one metadata read visits: the model-level facts and collections plus the
/// per-table sub-reads the table block assembles. The scope policy decides which of them a
/// read actually executes.
/// </summary>
public enum EPgMetadataSection
{
    /// <summary>Server version and database name scalars.</summary>
    ServerFacts,

    /// <summary>Extension discovery and the search-path widening that follows it.</summary>
    Extensions,

    /// <summary>Schema list.</summary>
    Schemas,

    /// <summary>Enum types.</summary>
    Enums,

    /// <summary>Domain types.</summary>
    Domains,

    /// <summary>Composite types (two statements).</summary>
    CompositeTypes,

    /// <summary>Sequences.</summary>
    Sequences,

    /// <summary>Tables (partitioned tables included).</summary>
    Tables,

    /// <summary>Views.</summary>
    Views,

    /// <summary>Materialized views.</summary>
    MaterializedViews,

    /// <summary>Functions and procedures.</summary>
    Functions,

    /// <summary>Triggers.</summary>
    Triggers,

    /// <summary>Row-level-security policies.</summary>
    Policies,

    /// <summary>Publications.</summary>
    Publications,

    /// <summary>Subscriptions.</summary>
    Subscriptions,

    /// <summary>Table columns.</summary>
    Columns,

    /// <summary>Table indexes.</summary>
    Indexes,

    /// <summary>Table foreign keys.</summary>
    ForeignKeys,

    /// <summary>Table check constraints.</summary>
    Checks,

    /// <summary>Table unique constraints.</summary>
    UniqueKeys,
}
