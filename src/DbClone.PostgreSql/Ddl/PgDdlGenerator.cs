using System.Text;

using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.PostgreSql.Execution;

namespace DbClone.PostgreSql.Ddl;

/// <summary>
/// PostgreSQL implementation of <see cref="IDdlGenerator"/>.
/// Generates DDL statements for all database object types.
/// </summary>
public sealed class PgDdlGenerator : IDdlGenerator
{
    /// <inheritdoc />
    public IReadOnlyList<string> GenerateCreateCompositeTypes(
        IEnumerable<CompositeTypeDefinition> types)
    {
        return types.Select(t =>
            {
                var attrs = string.Join(
                    ", ",
                    t.Attributes.Select(a =>
                        $"{PgIdentifierQuoter.QuoteIdentifier(a.Name)} {a.DataType}"));
                return
                    $"CREATE TYPE {PgIdentifierQuoter.QuoteSchemaQualified(t.SchemaName, t.Name)} AS ({attrs});";
            }).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GenerateCreateDomains(IEnumerable<DomainDefinition> domains)
    {
        return domains.Select(d =>
            {
                var sb = new StringBuilder();
                sb.Append(
                    $"CREATE DOMAIN {PgIdentifierQuoter.QuoteSchemaQualified(d.SchemaName, d.Name)} AS {d.DataType}");

                if (!string.IsNullOrEmpty(d.DefaultValue))
                    sb.Append($" DEFAULT {d.DefaultValue}");

                if (!d.IsNullable)
                    sb.Append(" NOT NULL");

                if (!string.IsNullOrEmpty(d.CheckExpression))
                    sb.Append($" {d.CheckExpression}");

                sb.Append(';');
                return sb.ToString();
            }).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GenerateCreateEnums(IEnumerable<EnumDefinition> enums)
    {
        return enums.Select(e =>
            {
                var labels = string.Join(", ", e.Labels.Select(l => $"'{EscapeString(l)}'"));
                return
                    $"CREATE TYPE {PgIdentifierQuoter.QuoteSchemaQualified(e.SchemaName, e.Name)} AS ENUM ({labels});";
            }).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GenerateCreateFunctions(IEnumerable<FunctionDefinition> functions)
    {
        return functions.Select(f =>
            {
                // pg_get_functiondef already returns the full CREATE FUNCTION/PROCEDURE statement
                var def = f.Definition.TrimEnd(';');
                return $"{def};";
            }).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GenerateCreateIndexes(
        IEnumerable<IndexDefinition> indexes,
        string schemaName,
        string tableName)
    {
        return indexes.Select(idx =>
            {
                if (idx.IsPrimary)
                {
                    // Primary keys are already part of CREATE TABLE
                    return "";
                }

                if (idx.IsConstraint)
                {
                    // Constraint-backed indexes (UNIQUE, EXCLUSION) are already created
                    // by the inline CONSTRAINT clause in CREATE TABLE
                    return "";
                }

                // Prefer the verbatim pg_get_indexdef output — it correctly handles
                // expression indexes, operator classes, collations, and sort options.
                if (!string.IsNullOrEmpty(idx.Definition))
                {
                    var def = idx.Definition.TrimEnd(';');
                    if (!string.IsNullOrEmpty(idx.Tablespace))
                        def += $" TABLESPACE {PgIdentifierQuoter.QuoteIdentifier(idx.Tablespace)}";
                    return def + ";";
                }

                // Fallback: reconstruct from parsed columns (simple column indexes only)
                var sb = new StringBuilder();
                sb.Append("CREATE ");

                if (idx.IsUnique)
                    sb.Append("UNIQUE ");

                sb.Append("INDEX ");
                sb.Append($"{PgIdentifierQuoter.QuoteIdentifier(idx.Name)}");
                sb.Append($" ON {PgIdentifierQuoter.QuoteSchemaQualified(schemaName, tableName)}");

                var columns = string.Join(
                    ", ",
                    idx.Columns.Select(PgIdentifierQuoter.QuoteIdentifier));
                sb.Append($" ({columns})");

                if (!string.IsNullOrEmpty(idx.Tablespace))
                    sb.Append($" TABLESPACE {PgIdentifierQuoter.QuoteIdentifier(idx.Tablespace)}");

                if (!string.IsNullOrEmpty(idx.FilterExpression))
                    sb.Append($" WHERE {idx.FilterExpression}");

                sb.Append(';');
                return sb.ToString();
            }).Where(s => !string.IsNullOrEmpty(s)).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GenerateCreateMaterializedViews(
        IEnumerable<MaterializedViewDefinition> views)
    {
        return views.Select(v =>
            {
                var def = v.Definition.TrimEnd(';');
                var sb = new StringBuilder();
                sb.Append(
                    $"CREATE MATERIALIZED VIEW {PgIdentifierQuoter.QuoteSchemaQualified(v.SchemaName, v.Name)} AS{Environment.NewLine}{def}");

                if (!string.IsNullOrEmpty(v.Tablespace))
                    sb.Append($" TABLESPACE {PgIdentifierQuoter.QuoteIdentifier(v.Tablespace)}");

                sb.Append(" WITH NO DATA;");
                return sb.ToString();
            }).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GenerateCreateSchemas(IEnumerable<SchemaDefinition> schemas)
    {
        return schemas.Select(s =>
            $"CREATE SCHEMA IF NOT EXISTS {PgIdentifierQuoter.QuoteIdentifier(s.Name)};"
        ).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GenerateCreateSequences(IEnumerable<SequenceDefinition> sequences)
    {
        return sequences.Select(s =>
            {
                var sb = new StringBuilder();
                sb.Append(
                    $"CREATE SEQUENCE {PgIdentifierQuoter.QuoteSchemaQualified(s.SchemaName, s.Name)}");

                if (!string.IsNullOrEmpty(s.DataType))
                    sb.Append($" AS {s.DataType}");

                sb.Append($" INCREMENT BY {s.IncrementBy}");
                sb.Append($" START WITH {s.StartValue}");

                if (s.MinValue.HasValue)
                    sb.Append($" MINVALUE {s.MinValue.Value}");
                else
                    sb.Append(" NO MINVALUE");

                if (s.MaxValue.HasValue)
                    sb.Append($" MAXVALUE {s.MaxValue.Value}");
                else
                    sb.Append(" NO MAXVALUE");

                sb.Append($" CACHE {s.CacheSize}");

                if (s.IsCycled)
                    sb.Append(" CYCLE");

                sb.Append(';');
                return sb.ToString();
            }).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GenerateCreateTables(IEnumerable<TableDefinition> tables)
    {
        return GenerateCreateTableStatements(tables).Select(p => p.Sql).ToList();
    }

    /// <summary>
    /// Generates CREATE TABLE statements ordered so that parent tables are always
    /// emitted before their partitions (required for PARTITION OF), including
    /// multi-level partitioning. Each entry carries the table name for error reporting.
    /// </summary>
    public IReadOnlyList<(string TableName, string Sql)> GenerateCreateTableStatements(
        IEnumerable<TableDefinition> tables)
    {
        var result = new List<(string, string)>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = tables.ToList();

        var madeProgress = true;
        while (pending.Count > 0 && madeProgress)
        {
            madeProgress = false;
            var remaining = new List<TableDefinition>();

            foreach (var table in pending)
            {
                if (table.ParentTable is null || emitted.Contains(table.ParentTable))
                {
                    result.Add(($"{table.SchemaName}.{table.Name}", GenerateCreateTable(table)));
                    emitted.Add($"{table.SchemaName}.{table.Name}");
                    madeProgress = true;
                }
                else
                {
                    remaining.Add(table);
                }
            }

            pending = remaining;
        }

        // Orphaned partitions (parent not present in the model) — emit standalone as best effort
        foreach (var table in pending)
        {
            result.Add(($"{table.SchemaName}.{table.Name}", GenerateCreateTable(table)));
        }

        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GenerateCreateTriggers(IEnumerable<TriggerDefinition> triggers)
    {
        return triggers.Select(t =>
            {
                var events = string.Join(" OR ", t.Events);
                var sb = new StringBuilder();
                sb.Append($"CREATE TRIGGER {PgIdentifierQuoter.QuoteIdentifier(t.Name)}");
                sb.Append($" {t.Timing} {events}");
                sb.Append(
                    $" ON {PgIdentifierQuoter.QuoteSchemaQualified(t.SchemaName, t.TableName)}");

                if (t.IsRowLevel)
                    sb.Append(" FOR EACH ROW");
                else
                    sb.Append(" FOR EACH STATEMENT");

                sb.Append(
                    $" EXECUTE FUNCTION {PgIdentifierQuoter.QuoteSchemaQualified(t.FunctionSchema, t.FunctionName)}();");

                return sb.ToString();
            }).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GenerateCreateViews(IEnumerable<ViewDefinition> views)
    {
        return views.Select(v =>
            {
                var def = v.Definition.TrimEnd(';');
                return
                    $"CREATE OR REPLACE VIEW {PgIdentifierQuoter.QuoteSchemaQualified(v.SchemaName, v.Name)} AS{Environment.NewLine}{def};";
            }).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GenerateForeignKeys(
        IEnumerable<ForeignKeyDefinition> foreignKeys,
        string schemaName,
        string tableName)
    {
        return foreignKeys.Select(fk =>
            {
                var columns = string.Join(
                    ", ",
                    fk.Columns.Select(PgIdentifierQuoter.QuoteIdentifier));
                var refColumns = string.Join(
                    ", ",
                    fk.ReferencedColumns.Select(PgIdentifierQuoter.QuoteIdentifier));

                var sb = new StringBuilder();
                sb.Append(
                    $"ALTER TABLE {PgIdentifierQuoter.QuoteSchemaQualified(schemaName, tableName)}");
                sb.Append($" ADD CONSTRAINT {PgIdentifierQuoter.QuoteIdentifier(fk.Name)}");
                sb.Append($" FOREIGN KEY ({columns})");
                sb.Append(
                    $" REFERENCES {PgIdentifierQuoter.QuoteSchemaQualified(fk.ReferencedSchema, fk.ReferencedTable)} ({refColumns})");
                sb.Append($" ON UPDATE {fk.UpdateRule}");
                sb.Append($" ON DELETE {fk.DeleteRule}");

                if (fk.IsDeferrable)
                {
                    sb.Append(" DEFERRABLE");
                    if (fk.IsInitiallyDeferred)
                        sb.Append(" INITIALLY DEFERRED");
                }

                sb.Append(';');
                return sb.ToString();
            }).ToList();
    }

    /// <inheritdoc />
    public string GenerateSetSequenceValue(string schemaName, string sequenceName, long value)
    {
        // setval's text argument is cast to regclass and parsed like an unquoted
        // identifier (case-folded to lowercase) — the name must be quoted inside
        // the literal to preserve case and special characters.
        var qualified = EscapeString(PgIdentifierQuoter.QuoteSchemaQualified(schemaName, sequenceName));
        return $"SELECT setval('{qualified}', {value}, true);";
    }

    private static string EscapeString(string value) => value.Replace("'", "''");

    /// <summary>
    /// Removes a trailing " NOT VALID" suffix from a constraint expression.
    /// <c>pg_get_constraintdef</c> appends it for unvalidated constraints, but
    /// inline CHECK constraints in CREATE TABLE do not support the clause.
    /// </summary>
    private static string StripNotValid(string expression)
    {
        const string suffix = " NOT VALID";
        return expression.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? expression[..^suffix.Length].TrimEnd()
            : expression;
    }

    private static string GenerateColumnDef(ColumnDefinition col)
    {
        var sb = new StringBuilder();
        sb.Append($"{PgIdentifierQuoter.QuoteIdentifier(col.Name)} {col.DataType}");

        if (col.IsIdentity)
        {
            sb.Append(" GENERATED BY DEFAULT AS IDENTITY");
        }
        else if (col.IsGenerated && !string.IsNullOrEmpty(col.GenerationExpression))
        {
            sb.Append($" GENERATED ALWAYS AS ({col.GenerationExpression}) STORED");
        }
        else if (!string.IsNullOrEmpty(col.DefaultValue))
        {
            sb.Append($" DEFAULT {col.DefaultValue}");
        }

        if (!col.IsNullable && !col.IsIdentity && !col.IsGenerated)
        {
            sb.Append(" NOT NULL");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates CREATE TABLE statement for a single table.
    /// Child partitions are created with PARTITION OF (columns, constraints and indexes
    /// are inherited from the parent); partitioned tables get a PARTITION BY clause.
    /// </summary>
    private static string GenerateCreateTable(TableDefinition table)
    {
        var sb = new StringBuilder();
        var tableName = PgIdentifierQuoter.QuoteSchemaQualified(table.SchemaName, table.Name);

        if (table.ParentTable is not null && table.PartitionBound is not null)
        {
            sb.Append(
                $"CREATE TABLE IF NOT EXISTS {tableName} PARTITION OF {ParseQualifiedTableName(table.ParentTable)} {table.PartitionBound}");

            // Sub-partitioned tables are both a partition and a parent
            if (table.IsPartitioned && !string.IsNullOrEmpty(table.PartitionStrategy))
                sb.Append($" PARTITION BY {table.PartitionStrategy}");

            return sb.Append(';').ToString();
        }

        if (table.ParentTable is not null)
        {
            // Legacy INHERITS child (has a parent but no partition bound). Declare
            // only the columns local to the child; inherited columns come from the
            // parent. GenerateCreateTableStatements emits the parent first.
            var localColumns =
                table.Columns.Where(c => c.IsLocal).Select(GenerateColumnDef).ToList();
            sb.Append($"CREATE TABLE IF NOT EXISTS {tableName} (");
            sb.Append(string.Join(",\n    ", localColumns));
            sb.Append($") INHERITS ({ParseQualifiedTableName(table.ParentTable)})");
            return sb.Append(';').ToString();
        }

        sb.Append($"CREATE TABLE IF NOT EXISTS {tableName} (");

        var columnDefs = table.Columns.Select(GenerateColumnDef).ToList();

        // Add primary key constraint inline
        var pk = table.Indexes.FirstOrDefault(i => i.IsPrimary);
        if (pk != null)
        {
            var pkCols = string.Join(", ", pk.Columns.Select(PgIdentifierQuoter.QuoteIdentifier));
            columnDefs.Add(
                $"CONSTRAINT {PgIdentifierQuoter.QuoteIdentifier(pk.Name)} PRIMARY KEY ({pkCols})");
        }

        // Add unique constraints inline
        foreach (var uq in table.UniqueConstraints)
        {
            var uqCols = string.Join(", ", uq.Columns.Select(PgIdentifierQuoter.QuoteIdentifier));
            columnDefs.Add(
                $"CONSTRAINT {PgIdentifierQuoter.QuoteIdentifier(uq.Name)} UNIQUE ({uqCols})");
        }

        // Add check constraints inline.
        // pg_get_constraintdef appends " NOT VALID" for unvalidated constraints,
        // but that clause is only legal in ALTER TABLE ADD CONSTRAINT — strip it.
        foreach (var chk in table.CheckConstraints)
        {
            var expr = StripNotValid(chk.Expression);
            columnDefs.Add(
                $"CONSTRAINT {PgIdentifierQuoter.QuoteIdentifier(chk.Name)} {expr}");
        }

        sb.Append(string.Join(",\n    ", columnDefs));
        sb.Append(')');

        if (table.IsPartitioned && !string.IsNullOrEmpty(table.PartitionStrategy))
            sb.Append($" PARTITION BY {table.PartitionStrategy}");

        return sb.Append(';').ToString();
    }

    /// <summary>
    /// Parses a "schema.table" key into a schema-qualified, quoted identifier.
    /// </summary>
    private static string ParseQualifiedTableName(string qualifiedName)
    {
        var dot = qualifiedName.IndexOf('.');
        if (dot <= 0 || dot == qualifiedName.Length - 1)
            return PgIdentifierQuoter.QuoteIdentifier(qualifiedName);

        return PgIdentifierQuoter.QuoteSchemaQualified(
            qualifiedName[..dot],
            qualifiedName[(dot + 1)..]);
    }
}
