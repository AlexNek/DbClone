namespace DbAdapter.Definitions.Interchange;

/// <summary>
/// Defines what can and cannot be converted between database dialects.
/// Objects are classified by their portability.
/// </summary>
public static class ObjectPortability
{
    /// <summary>
    /// Objects that can be converted between dialects (with type mapping).
    /// These have universal concepts that all databases support.
    /// </summary>
    public static readonly IReadOnlySet<string> ConvertibleObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "schema",
        "table",
        "column",
        "primaryKey",
        "foreignKey",
        "uniqueConstraint",
        "checkConstraint",
        "index",
        "data",
    };

    /// <summary>
    /// Objects that CANNOT be converted between dialects.
    /// These contain dialect-specific procedural code or features.
    /// They must be preserved as native text and only applied to same-dialect targets.
    /// </summary>
    public static readonly IReadOnlySet<string> NonConvertibleObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "function",       // PL/pgSQL, T-SQL, PL/SQL - different languages
        "procedure",      // Same as function
        "trigger",        // Procedural code, dialect-specific
        "view",           // May use dialect-specific functions
        "extension",      // PostgreSQL-specific concept
        "materializedView", // Implementation differs greatly
        "policy",         // PostgreSQL RLS - dialect-specific
        "publication",    // PostgreSQL logical replication
        "subscription",   // PostgreSQL logical replication
        "sequence",       // Syntax varies, not all DBs have them
        "customType",     // ENUM, COMPOSITE - dialect-specific
        "domain",         // PostgreSQL-specific
    };

    /// <summary>
    /// Determines if an object type can be converted between dialects.
    /// </summary>
    public static bool IsConvertible(string objectKind) =>
        ConvertibleObjects.Contains(objectKind);

    /// <summary>
    /// Determines if an object type must be preserved as native text.
    /// </summary>
    public static bool IsNonConvertible(string objectKind) =>
        NonConvertibleObjects.Contains(objectKind);

    /// <summary>
    /// Gets the conversion strategy for an object type.
    /// </summary>
    public static EConversionStrategy GetStrategy(string objectKind)
    {
        if (IsConvertible(objectKind))
            return EConversionStrategy.ConvertWithMapping;
        
        if (IsNonConvertible(objectKind))
            return EConversionStrategy.PreserveNativeOnly;
        
        // Unknown objects are preserved as-is
        return EConversionStrategy.PreserveNativeOnly;
    }
}

/// <summary>
/// How an object should be handled during cross-dialect transfer.
/// </summary>
public enum EConversionStrategy
{
    /// <summary>
    /// The object can be converted using type mappings and syntax transformation.
    /// </summary>
    ConvertWithMapping,

    /// <summary>
    /// The object must be preserved as native text and can only be applied
    /// to targets of the same dialect. Cross-dialect application is refused.
    /// </summary>
    PreserveNativeOnly,
}
