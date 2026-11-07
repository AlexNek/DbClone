namespace DbAdapter.Definitions.Interchange;

/// <summary>
/// Checks dialect compatibility for native object application.
/// Native objects can only be applied to targets of the same dialect.
/// </summary>
public static class DialectCompatibility
{
    /// <summary>
    /// Determines if native objects from the source dialect can be applied to the target dialect.
    /// </summary>
    /// <param name="sourceDialect">The dialect the objects came from (e.g., "postgresql").</param>
    /// <param name="targetDialect">The dialect of the target database (e.g., "postgresql").</param>
    /// <returns>True if the dialects match and native objects can be applied; false otherwise.</returns>
    public static bool CanApplyNativeObjects(string sourceDialect, string targetDialect)
    {
        if (string.IsNullOrWhiteSpace(sourceDialect) || string.IsNullOrWhiteSpace(targetDialect))
        {
            return false;
        }

        return string.Equals(sourceDialect, targetDialect, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the reason why native objects cannot be applied (if applicable).
    /// </summary>
    /// <param name="sourceDialect">The source dialect.</param>
    /// <param name="targetDialect">The target dialect.</param>
    /// <returns>A human-readable reason, or null if application is allowed.</returns>
    public static string? GetIncompatibilityReason(string sourceDialect, string targetDialect)
    {
        if (CanApplyNativeObjects(sourceDialect, targetDialect))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(sourceDialect))
        {
            return "Source dialect is not recorded in the catalog.";
        }

        if (string.IsNullOrWhiteSpace(targetDialect))
        {
            return "Target dialect is not specified.";
        }

        return $"Native objects from '{sourceDialect}' cannot be applied to '{targetDialect}'. " +
               $"Cross-dialect transfer of dialect-specific objects (functions, triggers, views) is not supported. " +
               $"Only portable objects (tables, columns, constraints, data) can be converted.";
    }

    /// <summary>
    /// Gets a list of object kinds that are dialect-specific and cannot be converted.
    /// </summary>
    public static IReadOnlyList<string> GetNonConvertibleKinds()
    {
        return ObjectPortability.NonConvertibleObjects.ToList();
    }
}
