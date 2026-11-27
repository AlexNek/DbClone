namespace CanonicalModel.Interchange;

/// <summary>
/// The kind of a schema object in the interchange model.
/// </summary>
public enum EObjectKind
{
    /// <summary>A database.</summary>
    Database,

    /// <summary>A schema.</summary>
    Schema,

    /// <summary>A table (including partitioned, foreign, and temporary).</summary>
    Table,

    /// <summary>A column.</summary>
    Column,

    /// <summary>A view.</summary>
    View,

    /// <summary>A materialized view.</summary>
    MaterializedView,

    /// <summary>A function.</summary>
    Function,

    /// <summary>A procedure.</summary>
    Procedure,

    /// <summary>An aggregate function.</summary>
    Aggregate,

    /// <summary>A window function.</summary>
    WindowFunction,

    /// <summary>A trigger.</summary>
    Trigger,

    /// <summary>A sequence.</summary>
    Sequence,

    /// <summary>A domain.</summary>
    Domain,

    /// <summary>An enum type.</summary>
    Enum,

    /// <summary>A composite type.</summary>
    CompositeType,

    /// <summary>A range type.</summary>
    RangeType,

    /// <summary>A collation.</summary>
    Collation,

    /// <summary>An operator.</summary>
    Operator,

    /// <summary>A cast.</summary>
    Cast,

    /// <summary>An extension.</summary>
    Extension,

    /// <summary>A primary key constraint.</summary>
    PrimaryKey,

    /// <summary>A unique constraint.</summary>
    UniqueConstraint,

    /// <summary>A check constraint.</summary>
    CheckConstraint,

    /// <summary>A foreign key constraint.</summary>
    ForeignKey,

    /// <summary>An exclusion constraint.</summary>
    ExclusionConstraint,

    /// <summary>An index.</summary>
    Index,

    /// <summary>A policy (RLS).</summary>
    Policy,

    /// <summary>A publication.</summary>
    Publication,

    /// <summary>A subscription.</summary>
    Subscription,

    /// <summary>A role or user.</summary>
    Role,

    /// <summary>A grant/permission.</summary>
    Grant,

    /// <summary>A relationship (dependency edge).</summary>
    Relationship,
}
