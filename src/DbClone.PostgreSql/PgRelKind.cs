namespace DbClone.PostgreSql;

/// <summary>
/// Named constants for PostgreSQL <c>pg_class.relkind</c> values.
/// See: https://www.postgresql.org/docs/current/catalog-pg-class.html
/// </summary>
public static class PgRelKind
{
    /// <summary>Ordinary table.</summary>
    public const string Table = "r";

    /// <summary>Partitioned table.</summary>
    public const string PartitionedTable = "p";

    /// <summary>View.</summary>
    public const string View = "v";

    /// <summary>Materialized view.</summary>
    public const string MaterializedView = "m";

    /// <summary>Composite type (user-defined via CREATE TYPE … AS).</summary>
    public const string CompositeType = "c";

    /// <summary>Sequence.</summary>
    public const string Sequence = "S";

    /// <summary>Index.</summary>
    public const string Index = "i";

    /// <summary>Partitioned index.</summary>
    public const string PartitionedIndex = "I";

    /// <summary>Foreign table.</summary>
    public const string ForeignTable = "f";

    // ── Pre-formatted SQL fragments (composed from the constants above) ──

    /// <summary>SQL IN-clause for all table-like relations.</summary>
    public const string TableOrPartition = "'" + Table + "', '" + PartitionedTable + "'";

    /// <summary>SQL IN-clause for all view-like relations.</summary>
    public const string ViewOrMaterialized = "'" + View + "', '" + MaterializedView + "'";

    /// <summary>SQL IN-clause for all user-visible relation kinds (tables + partitioned tables + views + matviews).</summary>
    public const string AllUserRelations = TableOrPartition + ", " + ViewOrMaterialized;
}
