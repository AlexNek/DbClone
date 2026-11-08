using System.Text.RegularExpressions;

using CanonicalModel.Interchange;

namespace DbAdapter.Definitions.Interchange.Converters.Samples;

/// <summary>
/// Sample converter that converts PostgreSQL functions to MySQL functions.
/// This is a simplified example showing the converter pattern.
/// </summary>
/// <remarks>
/// <para>
/// This converter handles basic PL/pgSQL to MySQL function conversion:
/// - Converts function syntax (CREATE FUNCTION ... RETURNS → CREATE FUNCTION ... RETURNS)
/// - Maps common data types (SERIAL → INT AUTO_INCREMENT, TEXT → VARCHAR, etc.)
/// - Converts PL/pgSQL control structures to MySQL equivalents
/// </para>
/// <para>
/// Limitations:
/// - Does not handle all PL/pgSQL features (cursors, exceptions, etc.)
/// - Complex functions may require manual conversion
/// - Some PostgreSQL-specific functions have no MySQL equivalent
/// </para>
/// </remarks>
public sealed partial class PostgreSqlToMySqlFunctionConverter : IObjectConverter
{
    /// <inheritdoc />
    public string SourceDialect => "postgresql";

    /// <inheritdoc />
    public string TargetDialect => "mysql";

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedObjectKinds => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "function",
        "procedure",
    };

    /// <inheritdoc />
    public bool CanConvert(string objectKind) => SupportedObjectKinds.Contains(objectKind);

    /// <inheritdoc />
    public Task<NativeObjectDefinition?> ConvertAsync(
        NativeObjectDefinition source,
        ConversionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        // The dialect travels with the captured text rather than with the object, and an object whose
        // statement was never recorded has no text here to convert.
        if (source.NativeDefinition is not { } sourceText || sourceText.Dialect != SourceDialect)
        {
            throw new ArgumentException(
                $"Expected source dialect '{SourceDialect}', got '{source.NativeDefinition?.Dialect}'.",
                nameof(source));
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            var convertedSql = ConvertFunction(sourceText.Text, context);

            // The rewrite is a pattern substitution over the source statement, so the result is
            // recorded as a fragment: nothing here has checked that it parses on the target engine.
            var result = new NativeObjectDefinition
            {
                Ref = source.Ref,
                Kind = source.Kind,
                SchemaName = string.Empty, // MySQL doesn't have schemas in the same way
                Name = source.Name,
                SourceObjectId = source.SourceObjectId,
                Dependencies = source.Dependencies,
                NativeDefinition = new NativeDefinition(
                    TargetDialect,
                    convertedSql,
                    Complete: false,
                    CapturedBy: "converted"),
                Notes = [
                    new InterchangeIssue(
                        "ConvertedByRegex",
                        EIssueSeverity.Info,
                        $"The {TargetDialect} statement of {source.Name} was rewritten from the "
                        + "source statement by pattern substitution and was not checked against the "
                        + "target engine."),
                ],
            };

            return Task.FromResult<NativeObjectDefinition?>(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ConversionFailedException(
                $"Failed to convert {source.Kind} '{source.SchemaName}.{source.Name}': {ex.Message}",
                source.Kind.ToString(),
                SourceDialect,
                TargetDialect,
                $"{source.SchemaName}.{source.Name}",
                ex);
        }
    }

    private static string ConvertFunction(string pgSql, ConversionContext context)
    {
        var sql = pgSql;

        // Convert CREATE FUNCTION syntax
        sql = ConvertCreateFunctionSyntax(sql);

        // Convert data types
        if (context.Options?.ConvertDataTypes != false)
        {
            sql = ConvertDataTypes(sql, context.Options?.TypeMappings);
        }

        // Convert PL/pgSQL to MySQL control structures
        if (context.Options?.ConvertFunctions != false)
        {
            sql = ConvertControlStructures(sql);
            sql = ConvertBuiltInFunctions(sql);
        }

        return sql;
    }

    private static string ConvertCreateFunctionSyntax(string sql)
    {
        // PostgreSQL: CREATE FUNCTION name(params) RETURNS type AS $$ ... $$ LANGUAGE plpgsql;
        // MySQL:      CREATE FUNCTION name(params) RETURNS type BEGIN ... END;

        // Remove LANGUAGE plpgsql clause
        sql = LanguagePattern().Replace(sql, string.Empty);

        // Replace $$ delimiters with BEGIN...END
        sql = DollarDelimiterPattern().Replace(sql, match =>
        {
            var body = match.Groups[1].Value;
            return $"BEGIN\n{body}\nEND";
        });

        // Convert RETURNS TABLE(...) to RETURNS type (simplified - MySQL doesn't support table returns)
        sql = ReturnsTablePattern().Replace(sql, "RETURNS INT");

        return sql;
    }

    private static string ConvertDataTypes(string sql, IReadOnlyDictionary<string, string>? customMappings)
    {
        // Apply custom mappings first
        if (customMappings is not null)
        {
            foreach (var mapping in customMappings)
            {
                sql = Regex.Replace(sql, $@"\b{Regex.Escape(mapping.Key)}\b", mapping.Value, RegexOptions.IgnoreCase);
            }
        }

        // Common PostgreSQL → MySQL type mappings
        sql = Regex.Replace(sql, @"\bSERIAL\b", "INT AUTO_INCREMENT", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bBIGSERIAL\b", "BIGINT AUTO_INCREMENT", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bSMALLSERIAL\b", "SMALLINT AUTO_INCREMENT", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bTEXT\b", "TEXT", RegexOptions.IgnoreCase); // Same in MySQL
        sql = Regex.Replace(sql, @"\bBYTEA\b", "BLOB", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bBOOLEAN\b", "TINYINT(1)", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bTIMESTAMP WITH TIME ZONE\b", "TIMESTAMP", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bTIMESTAMP WITHOUT TIME ZONE\b", "DATETIME", RegexOptions.IgnoreCase);

        return sql;
    }

    private static string ConvertControlStructures(string sql)
    {
        // Convert IF ... THEN ... END IF;
        // PostgreSQL and MySQL have similar syntax, but MySQL requires END IF; (with semicolon)
        sql = Regex.Replace(sql, @"\bEND IF\b", "END IF;", RegexOptions.IgnoreCase);

        // Convert FOR loops (simplified)
        // PostgreSQL: FOR i IN 1..10 LOOP ... END LOOP;
        // MySQL:      FOR i IN 1..10 DO ... END FOR;
        sql = Regex.Replace(sql, @"\bLOOP\b", "DO", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bEND LOOP\b", "END FOR", RegexOptions.IgnoreCase);

        // Convert RETURN to RETURN (same, but ensure no semicolon issues)
        // PostgreSQL: RETURN value;
        // MySQL:      RETURN value;

        return sql;
    }

    private static string ConvertBuiltInFunctions(string sql)
    {
        // Convert common PostgreSQL functions to MySQL equivalents
        sql = Regex.Replace(sql, @"\bCURRENT_TIMESTAMP\b", "NOW()", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bNOW\(\)\b", "NOW()", RegexOptions.IgnoreCase); // Same
        sql = Regex.Replace(sql, @"\bLENGTH\(\b", "LENGTH(", RegexOptions.IgnoreCase); // Same
        sql = Regex.Replace(sql, @"\bUPPER\(\b", "UPPER(", RegexOptions.IgnoreCase); // Same
        sql = Regex.Replace(sql, @"\bLOWER\(\b", "LOWER(", RegexOptions.IgnoreCase); // Same

        // PostgreSQL-specific functions that need conversion
        sql = Regex.Replace(sql, @"\bARRAY_AGG\(\b", "GROUP_CONCAT(", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bSTRING_AGG\(([^,]+),\s*([^)]+)\)", "GROUP_CONCAT($1 SEPARATOR $2)", RegexOptions.IgnoreCase);

        return sql;
    }

    [GeneratedRegex(@"\s*LANGUAGE\s+\w+\s*;?", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex LanguagePattern();

    [GeneratedRegex(@"\$\$(.*?)\$\$", RegexOptions.Singleline)]
    private static partial Regex DollarDelimiterPattern();

    [GeneratedRegex(@"RETURNS\s+TABLE\s*\([^)]*\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ReturnsTablePattern();
}
