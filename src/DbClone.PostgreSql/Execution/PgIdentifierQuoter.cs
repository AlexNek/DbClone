namespace DbClone.PostgreSql.Execution;

/// <summary>
/// Utility for safely quoting PostgreSQL identifiers.
/// </summary>
public static class PgIdentifierQuoter
{
    // PostgreSQL reserved words that require quoting
    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
                                                                {
                                                                    "all",
                                                                    "analyse",
                                                                    "analyze",
                                                                    "and",
                                                                    "any",
                                                                    "array",
                                                                    "as",
                                                                    "asc",
                                                                    "asymmetric",
                                                                    "both",
                                                                    "case",
                                                                    "cast",
                                                                    "check",
                                                                    "collate",
                                                                    "column",
                                                                    "constraint",
                                                                    "create",
                                                                    "current_catalog",
                                                                    "current_date",
                                                                    "current_role",
                                                                    "current_time",
                                                                    "current_timestamp",
                                                                    "current_user",
                                                                    "default",
                                                                    "deferrable",
                                                                    "desc",
                                                                    "distinct",
                                                                    "do",
                                                                    "else",
                                                                    "end",
                                                                    "except",
                                                                    "false",
                                                                    "fetch",
                                                                    "for",
                                                                    "foreign",
                                                                    "from",
                                                                    "grant",
                                                                    "group",
                                                                    "having",
                                                                    "in",
                                                                    "initially",
                                                                    "intersect",
                                                                    "into",
                                                                    "lateral",
                                                                    "leading",
                                                                    "limit",
                                                                    "localtime",
                                                                    "localtimestamp",
                                                                    "not",
                                                                    "null",
                                                                    "offset",
                                                                    "on",
                                                                    "only",
                                                                    "or",
                                                                    "order",
                                                                    "placing",
                                                                    "primary",
                                                                    "references",
                                                                    "returning",
                                                                    "select",
                                                                    "session_user",
                                                                    "some",
                                                                    "symmetric",
                                                                    "table",
                                                                    "then",
                                                                    "to",
                                                                    "trailing",
                                                                    "true",
                                                                    "union",
                                                                    "unique",
                                                                    "user",
                                                                    "using",
                                                                    "variadic",
                                                                    "when",
                                                                    "where",
                                                                    "window",
                                                                    "with",
                                                                    "authorization",
                                                                    "between",
                                                                    "cross",
                                                                    "freeze",
                                                                    "full",
                                                                    "ilike",
                                                                    "inner",
                                                                    "is",
                                                                    "isnull",
                                                                    "join",
                                                                    "left",
                                                                    "like",
                                                                    "natural",
                                                                    "notnull",
                                                                    "outer",
                                                                    "overlaps",
                                                                    "right",
                                                                    "similar",
                                                                    "verbose"
                                                                };

    /// <summary>
    /// Quotes a PostgreSQL identifier if needed.
    /// Returns bare identifier for simple lowercase names, quoted otherwise.
    /// </summary>
    public static string QuoteIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        if (string.IsNullOrEmpty(identifier))
            throw new ArgumentException("Identifier cannot be empty", nameof(identifier));

        // Already quoted
        if (identifier.StartsWith('"') && identifier.EndsWith('"'))
            return identifier;

        // Need quoting if:
        // 1. Contains special characters (non-alphanumeric/underscore)
        // 2. Starts with a digit
        // 3. Contains uppercase letters
        // 4. Is a reserved word
        if (NeedsQuoting(identifier))
        {
            // Escape any embedded double quotes by doubling them
            var escaped = identifier.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }

        return identifier;
    }

    /// <summary>
    /// Returns a schema-qualified, quoted identifier.
    /// </summary>
    public static string QuoteSchemaQualified(string schema, string name)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(name);

        return $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}";
    }

    /// <summary>
    /// Strips surrounding quotes from an identifier.
    /// </summary>
    public static string UnquoteIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        if (identifier.StartsWith('"') && identifier.EndsWith('"') && identifier.Length >= 2)
        {
            return identifier[1..^1].Replace("\"\"", "\"");
        }

        return identifier;
    }

    /// <summary>
    /// Determines if an identifier needs quoting.
    /// </summary>
    private static bool NeedsQuoting(string identifier)
    {
        if (ReservedWords.Contains(identifier))
            return true;

        if (char.IsDigit(identifier[0]))
            return true;

        if (identifier.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            return true;

        // Any uppercase letter means we need quoting to preserve case
        if (identifier.Any(char.IsUpper))
            return true;

        return false;
    }
}
