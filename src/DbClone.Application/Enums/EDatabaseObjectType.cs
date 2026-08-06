namespace DbClone.Application.Enums;

/// <summary>
/// Types of database objects.
/// </summary>
public enum EDatabaseObjectType
{
    /// <summary>Schema.</summary>
    Schema,

    /// <summary>Table.</summary>
    Table,

    /// <summary>Index.</summary>
    Index,

    /// <summary>View.</summary>
    View,

    /// <summary>Materialized view.</summary>
    MaterializedView,

    /// <summary>Function.</summary>
    Function,

    /// <summary>Trigger.</summary>
    Trigger,

    /// <summary>Sequence.</summary>
    Sequence,

    /// <summary>Enum type.</summary>
    Enum,

    /// <summary>Domain type.</summary>
    Domain,

    /// <summary>Composite type.</summary>
    CompositeType,

    /// <summary>Constraint (FK, check, unique).</summary>
    Constraint
}
