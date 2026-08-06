using System.Text.RegularExpressions;
using DbClone.Application.Enums;
using DbClone.Application.Compare;
using DbClone.Application.Models;

namespace DbClone.Application.Compare.Comparers;

/// <summary>
/// Compares the structural DDL of tables that exist on both sides:
/// columns (name, type, nullability, defaults, identity), primary keys,
/// foreign keys, check constraints, and unique constraints.
/// </summary>
public sealed partial class TableDdlComparer : IModelComparer
{
    public IReadOnlyList<ModelCompareItem> Compare(
        DatabaseModel source,
        DatabaseModel dest,
        CancellationToken ct)
    {
        var items = new List<ModelCompareItem>();

        var srcTableDict = source.Tables.ToDictionary(
            t => $"{t.SchemaName}.{t.Name}",
            t => t,
            StringComparer.OrdinalIgnoreCase);
        var dstTableDict = dest.Tables.ToDictionary(
            t => $"{t.SchemaName}.{t.Name}",
            t => t,
            StringComparer.OrdinalIgnoreCase);

        foreach (var (key, srcTable) in srcTableDict)
        {
            ct.ThrowIfCancellationRequested();
            if (!dstTableDict.TryGetValue(key, out var dstTable))
                continue; // presence differences reported by table data comparison

            var differences = new List<string>();
            var notices = new List<string>();

            CompareColumns(srcTable, dstTable, differences);
            ComparePrimaryKey(srcTable, dstTable, differences);
            CompareForeignKeys(srcTable, dstTable, differences);
            CompareCheckConstraints(srcTable, dstTable, differences, notices);
            CompareUniqueConstraints(srcTable, dstTable, differences);

            if (differences.Count > 0)
            {
                var allDetails = differences.Concat(notices).ToList();
                items.Add(new ModelCompareItem(
                    EDatabaseObjectType.Table,
                    srcTable.SchemaName,
                    key,
                    ECompareStatus.Different,
                    "DDL differs: " + string.Join("; ", allDetails)));
            }
            else if (notices.Count > 0)
            {
                items.Add(new ModelCompareItem(
                    EDatabaseObjectType.Table,
                    srcTable.SchemaName,
                    key,
                    ECompareStatus.Notice,
                    "DDL notice: " + string.Join("; ", notices)));
            }
        }

        return items;
    }

    private static void CompareColumns(
        TableDefinition src, TableDefinition dst, List<string> differences)
    {
        var srcCols = src.Columns
            .OrderBy(c => c.OrdinalPosition)
            .Select(GetColumnSignature)
            .ToList();
        var dstCols = dst.Columns
            .OrderBy(c => c.OrdinalPosition)
            .Select(GetColumnSignature)
            .ToList();

        if (srcCols.SequenceEqual(dstCols))
            return;

        var srcNames = src.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dstNames = dst.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = dstNames.Except(srcNames, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = srcNames.Except(dstNames, StringComparer.OrdinalIgnoreCase).ToList();
        var common = srcNames.Intersect(dstNames, StringComparer.OrdinalIgnoreCase).ToList();
        var modified = common.Where(name =>
            GetColumnSignature(src.Columns.First(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) !=
            GetColumnSignature(dst.Columns.First(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))).ToList();

        if (removed.Count > 0)
            differences.Add($"Columns removed: {string.Join(", ", removed)}");

        if (added.Count > 0)
            differences.Add($"Columns added: {string.Join(", ", added)}");

        foreach (var colName in modified)
        {
            var srcCol = src.Columns.First(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
            var dstCol = dst.Columns.First(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
            var changes = DescribeColumnChanges(srcCol, dstCol);
            differences.Add($"Column \"{colName}\": {string.Join(", ", changes)}");
        }
    }

    private static List<string> DescribeColumnChanges(ColumnDefinition src, ColumnDefinition dst)
    {
        var changes = new List<string>();

        if (!string.Equals(src.DataType, dst.DataType, StringComparison.OrdinalIgnoreCase))
            changes.Add($"type {src.DataType} → {dst.DataType}");

        if (src.IsNullable != dst.IsNullable)
            changes.Add(dst.IsNullable ? "nullable (was NOT NULL)" : "NOT NULL (was nullable)");

        if (!string.Equals(src.DefaultValue ?? "", dst.DefaultValue ?? "", StringComparison.Ordinal))
        {
            var srcDefault = string.IsNullOrEmpty(src.DefaultValue) ? "none" : src.DefaultValue;
            var dstDefault = string.IsNullOrEmpty(dst.DefaultValue) ? "none" : dst.DefaultValue;
            changes.Add($"default {srcDefault} → {dstDefault}");
        }

        if (src.IsIdentity != dst.IsIdentity)
            changes.Add(dst.IsIdentity ? "added IDENTITY" : "removed IDENTITY");

        if (src.IsGenerated != dst.IsGenerated)
            changes.Add(dst.IsGenerated ? "added GENERATED" : "removed GENERATED");

        if (src.IsGenerated && dst.IsGenerated &&
            !string.Equals(src.GenerationExpression ?? "", dst.GenerationExpression ?? "", StringComparison.Ordinal))
            changes.Add($"generation expr changed");

        if (changes.Count == 0)
            changes.Add("definition differs");

        return changes;
    }

    private static void ComparePrimaryKey(
        TableDefinition src, TableDefinition dst, List<string> differences)
    {
        var srcPk = src.Indexes.FirstOrDefault(i => i.IsPrimary);
        var dstPk = dst.Indexes.FirstOrDefault(i => i.IsPrimary);
        var srcPkCols = srcPk != null ? string.Join(", ", srcPk.Columns) : "";
        var dstPkCols = dstPk != null ? string.Join(", ", dstPk.Columns) : "";

        if (srcPkCols == dstPkCols)
            return;

        if (srcPk == null)
            differences.Add($"Primary key added in dest: ({dstPkCols})");
        else if (dstPk == null)
            differences.Add($"Primary key missing in dest (source: ({srcPkCols}))");
        else
            differences.Add($"Primary key differs: ({srcPkCols}) → ({dstPkCols})");
    }

    private static void CompareForeignKeys(
        TableDefinition src, TableDefinition dst, List<string> differences)
    {
        var srcByName = src.ForeignKeys.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var dstByName = dst.ForeignKeys.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        var srcNames = srcByName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dstNames = dstByName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = srcNames.Except(dstNames, StringComparer.OrdinalIgnoreCase).ToList();
        var added = dstNames.Except(srcNames, StringComparer.OrdinalIgnoreCase).ToList();
        var common = srcNames.Intersect(dstNames, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var name in removed)
            differences.Add($"FK removed: {name}");

        foreach (var name in added)
        {
            var fk = dstByName[name];
            differences.Add($"FK added: {name} → {fk.ReferencedSchema}.{fk.ReferencedTable}({string.Join(", ", fk.ReferencedColumns)})");
        }

        foreach (var name in common)
        {
            if (GetForeignKeySignature(srcByName[name]) != GetForeignKeySignature(dstByName[name]))
                differences.Add($"FK modified: {name}");
        }
    }

    private static void CompareCheckConstraints(
        TableDefinition src, TableDefinition dst,
        List<string> differences, List<string> notices)
    {
        var srcByName = src.CheckConstraints.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var dstByName = dst.CheckConstraints.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var srcNames = srcByName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dstNames = dstByName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = srcNames.Except(dstNames, StringComparer.OrdinalIgnoreCase).ToList();
        var added = dstNames.Except(srcNames, StringComparer.OrdinalIgnoreCase).ToList();
        var common = srcNames.Intersect(dstNames, StringComparer.OrdinalIgnoreCase).ToList();

        // Partition children inherit constraints from the parent; PostgreSQL may
        // normalize the decompiled expression differently per partition.  Added or
        // removed constraints on a partition child are therefore notices, not hard
        // differences — the parent is the source of truth.
        var isPartitionChild = !string.IsNullOrEmpty(src.ParentTable);

        foreach (var name in removed)
        {
            if (isPartitionChild)
                notices.Add($"CHECK missing on partition (inherited from parent): {name}");
            else
                differences.Add($"CHECK removed: {name}");
        }

        foreach (var name in added)
        {
            if (isPartitionChild)
                notices.Add($"CHECK extra on partition: {name} ({dstByName[name].Expression})");
            else
                differences.Add($"CHECK added: {name} ({dstByName[name].Expression})");
        }

        foreach (var name in common)
        {
            var srcExpr = srcByName[name].Expression;
            var dstExpr = dstByName[name].Expression;

            // Fast path: exact match.
            if (string.Equals(srcExpr, dstExpr, StringComparison.Ordinal))
                continue;

            // Normalize (collapse whitespace, strip redundant parentheses) and retry.
            var srcNorm = NormalizeCheckExpression(srcExpr);
            var dstNorm = NormalizeCheckExpression(dstExpr);
            if (srcNorm == dstNorm)
                continue; // logically equivalent — cosmetic decompiler difference

            // A real difference remains.  On partition children the constraint is
            // inherited and the divergence is almost certainly a PostgreSQL
            // normalization artifact, so downgrade to a notice.
            var detail = $"CHECK modified: {name} (source: {srcExpr}, dest: {dstExpr})";
            if (isPartitionChild)
                notices.Add(detail);
            else
                differences.Add(detail);
        }
    }

    /// <summary>
    /// Normalizes a <c>pg_get_constraintdef</c> expression for comparison:
    /// collapses whitespace, trims, strips redundant outer parentheses, and
    /// normalizes type-cast representations that differ across PostgreSQL versions.
    /// <para>
    /// PostgreSQL's decompiler may represent the same constraint differently depending
    /// on server version. For example, a CHECK with an ANY(ARRAY[...]) may appear as:
    /// <list type="bullet">
    ///   <item><c>(ARRAY['a'::varchar, 'b'::varchar])::text[]</c> (whole-array cast)</item>
    ///   <item><c>ARRAY[('a'::varchar)::text, ('b'::varchar)::text]</c> (per-element cast)</item>
    /// </list>
    /// Both are semantically identical. This method normalizes to the per-element form
    /// so that textual comparison succeeds.
    /// </para>
    /// </summary>
    internal static string NormalizeCheckExpression(string expression)
    {
        // Collapse all whitespace runs to a single space and trim.
        var result = WhitespaceRegex().Replace(expression.Trim(), " ");

        // Strip the leading "CHECK " keyword if present — we compare only the body.
        if (result.StartsWith("CHECK ", StringComparison.OrdinalIgnoreCase))
            result = result[6..].Trim();

        // Iteratively remove matching outer parentheses: ((expr)) → (expr) → expr.
        while (result.Length >= 2 && result[0] == '(' && result[^1] == ')' &&
               IsMatchingOuterParens(result))
        {
            result = result[1..^1].Trim();
        }

        // Normalize array type-cast representations.
        // Pattern: (ARRAY[elements])::targettype[]  →  ARRAY[(element)::targettype, ...]
        // This handles the PostgreSQL decompiler variance where some versions cast the
        // entire array while others cast each element individually.
        result = NormalizeArrayCasts(result);

        // Normalize redundant parentheses around simple cast expressions:
        // (expr)::type  →  expr::type  (when expr contains no operators)
        result = RedundantCastParensRegex().Replace(result, "$1::$2");

        return result;
    }

    /// <summary>
    /// Normalizes <c>(ARRAY[elem1::srctype, elem2::srctype, ...])::targettype[]</c>
    /// into <c>ARRAY[(elem1::srctype)::targettype, (elem2::srctype)::targettype, ...]</c>.
    /// This is the canonical per-element form. If the expression is already in per-element
    /// form or doesn't contain the whole-array cast pattern, it is returned unchanged.
    /// </summary>
    private static string NormalizeArrayCasts(string expr)
    {
        // Find all occurrences of (ARRAY[...])::type[] and convert them.
        // We use a manual scan because the array content can be complex (nested parens,
        // quoted strings with special chars).
        var result = expr;
        int searchFrom = 0;

        while (true)
        {
            // Look for "(ARRAY[" which signals a parenthesized array that may have a cast.
            var marker = result.IndexOf("(ARRAY[", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                break;

            // Find the matching ')' for the outer '(' at marker.
            int outerClose = FindMatchingParen(result, marker);
            if (outerClose < 0)
            {
                searchFrom = marker + 1;
                continue;
            }

            // After the ')' we expect '::' followed by a type name ending with '[]'.
            var afterParen = result[(outerClose + 1)..];
            var castMatch = ArrayCastSuffixRegex().Match(afterParen);
            if (!castMatch.Success)
            {
                searchFrom = marker + 1;
                continue;
            }

            var targetType = castMatch.Groups[1].Value; // e.g. "text"

            // Extract the ARRAY[...] content (without outer parens).
            // Inner part is from marker+1 to outerClose-1, which gives us "ARRAY[...]".
            var arrayExpr = result[(marker + 1)..outerClose]; // "ARRAY[elem1, elem2, ...]"

            // Find the opening '[' and extract elements.
            var bracketStart = arrayExpr.IndexOf('[');
            if (bracketStart < 0)
            {
                searchFrom = marker + 1;
                continue;
            }

            var bracketEnd = arrayExpr.LastIndexOf(']');
            if (bracketEnd < 0 || bracketEnd <= bracketStart)
            {
                searchFrom = marker + 1;
                continue;
            }

            var elementsStr = arrayExpr[(bracketStart + 1)..bracketEnd];
            var elements = SplitArrayElements(elementsStr);

            // Rewrite each element: element → (element)::targetType
            var rewritten = elements.Select(e =>
            {
                var trimmed = e.Trim();
                // If element already has ::targetType at the end, don't double-cast.
                if (trimmed.EndsWith($"::{targetType}", StringComparison.OrdinalIgnoreCase))
                    return trimmed;
                // If element is wrapped in parens and already cast, e.g. ('x'::varchar)::text
                if (trimmed.StartsWith("(") && trimmed.Contains($")::{targetType}", StringComparison.OrdinalIgnoreCase))
                    return trimmed;
                return $"({trimmed})::{targetType}";
            });

            var replacement = $"ARRAY[{string.Join(", ", rewritten)}]";
            var totalReplacedLength = (outerClose + 1 - marker) + castMatch.Length;
            result = string.Concat(result.AsSpan(0, marker), replacement, result.AsSpan(marker + totalReplacedLength));
            searchFrom = marker + replacement.Length;
        }

        return result;
    }

    /// <summary>
    /// Splits comma-separated array elements respecting parentheses depth and quoted strings.
    /// </summary>
    private static List<string> SplitArrayElements(string s)
    {
        var elements = new List<string>();
        int depth = 0;
        bool inQuote = false;
        int start = 0;

        for (int i = 0; i < s.Length; i++)
        {
            var ch = s[i];

            if (inQuote)
            {
                // Handle escaped quote ('') inside a string literal.
                if (ch == '\'' && i + 1 < s.Length && s[i + 1] == '\'')
                {
                    i++; // skip escaped quote
                    continue;
                }
                if (ch == '\'')
                    inQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inQuote = true;
                    break;
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    elements.Add(s[start..i]);
                    start = i + 1;
                    break;
            }
        }

        if (start < s.Length)
            elements.Add(s[start..]);

        return elements;
    }

    /// <summary>
    /// Finds the index of the closing ')' that matches the '(' at position <paramref name="openPos"/>.
    /// Respects nested parentheses and single-quoted string literals.
    /// Returns -1 if no match is found.
    /// </summary>
    private static int FindMatchingParen(string s, int openPos)
    {
        int depth = 0;
        bool inQuote = false;

        for (int i = openPos; i < s.Length; i++)
        {
            var ch = s[i];

            if (inQuote)
            {
                if (ch == '\'' && i + 1 < s.Length && s[i + 1] == '\'')
                {
                    i++;
                    continue;
                }
                if (ch == '\'')
                    inQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inQuote = true;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                        return i;
                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns true when the first '(' matches the last ')' — i.e. the entire
    /// expression is wrapped in one pair of parentheses, not "(a) AND (b)".
    /// </summary>
    private static bool IsMatchingOuterParens(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length - 1; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;

            // If depth returns to zero before the last char, the outer parens
            // don't wrap the whole expression.
            if (depth == 0)
                return false;
        }
        return true;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Matches the cast suffix after a parenthesized ARRAY expression: <c>::typename[]</c>.
    /// Captures the base type name (e.g. "text" from "::text[]").
    /// </summary>
    [GeneratedRegex(@"^::([a-z_][\w ]*)\[\]", RegexOptions.IgnoreCase)]
    private static partial Regex ArrayCastSuffixRegex();

    /// <summary>
    /// Matches redundant parentheses around a simple identifier/literal before a cast:
    /// <c>('value')::type</c> → <c>'value'::type</c>.
    /// Only applies to simple expressions (no operators, commas, or nested parens inside).
    /// </summary>
    [GeneratedRegex(@"\(([^(),]+)\)::([a-z_][\w ]*(?:\[\])?)", RegexOptions.IgnoreCase)]
    private static partial Regex RedundantCastParensRegex();

    private static void CompareUniqueConstraints(
        TableDefinition src, TableDefinition dst, List<string> differences)
    {
        var srcByName = src.UniqueConstraints.ToDictionary(u => u.Name, StringComparer.OrdinalIgnoreCase);
        var dstByName = dst.UniqueConstraints.ToDictionary(u => u.Name, StringComparer.OrdinalIgnoreCase);

        var srcNames = srcByName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dstNames = dstByName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = srcNames.Except(dstNames, StringComparer.OrdinalIgnoreCase).ToList();
        var added = dstNames.Except(srcNames, StringComparer.OrdinalIgnoreCase).ToList();
        var common = srcNames.Intersect(dstNames, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var name in removed)
            differences.Add($"UNIQUE removed: {name}");

        foreach (var name in added)
            differences.Add($"UNIQUE added: {name} ({string.Join(", ", dstByName[name].Columns)})");

        foreach (var name in common)
        {
            var srcCols = string.Join(",", srcByName[name].Columns);
            var dstCols = string.Join(",", dstByName[name].Columns);
            if (srcCols != dstCols)
                differences.Add($"UNIQUE modified: {name} ({srcCols}) → ({dstCols})");
        }
    }

    private static string GetColumnSignature(ColumnDefinition col) =>
        $"{col.Name}|{col.DataType}|{col.IsNullable}|{col.DefaultValue ?? ""}|{col.IsIdentity}|{col.IsGenerated}|{col.GenerationExpression ?? ""}";

    private static string GetForeignKeySignature(ForeignKeyDefinition fk) =>
        $"{fk.Name}|{string.Join(",", fk.Columns)}|{fk.ReferencedSchema}.{fk.ReferencedTable}|{string.Join(",", fk.ReferencedColumns)}|{fk.UpdateRule}|{fk.DeleteRule}";
}

