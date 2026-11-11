using System.Runtime.CompilerServices;
using System.Text;

using CanonicalModel.Interchange;

using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Execution;

namespace DbAdapter.PostgreSql.Interchange;

/// <summary>
/// PostgreSQL's <see cref="ISqlScriptRenderer"/>: the restore's own statements, guarded so the file may be
/// run more than once, plus the one insert shape the restore never emits.
/// </summary>
/// <remarks>
/// <para>
/// Every schema and every row statement here is the text <see cref="PostgreSqlWriter"/> already builds and
/// the restore already executes — this class adds no second DDL builder, it applies
/// <see cref="ScriptStatementGuard"/> to what that one produces (D1, D7). Only the keyless table's insert
/// is written here, because the restore's is <c>INSERT INTO … VALUES …</c> and no such statement can skip a
/// row it finds already present (D4).
/// </para>
/// <para>
/// No file is opened, no connection is made, and no transaction is begun: this role answers text, and the
/// product decides where the text goes.
/// </para>
/// </remarks>
internal sealed class PostgreSqlScriptRenderer : ISqlScriptRenderer
{
    private readonly string _dialect;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlScriptRenderer"/> class.
    /// </summary>
    /// <param name="dialect">
    /// The dialect the text is written in, taken from the adapter's own identity so the catalog can be
    /// compared against the one adapter that can execute what this role writes (D11).
    /// </param>
    internal PostgreSqlScriptRenderer(string dialect)
    {
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
    }

    /// <inheritdoc />
    public string Dialect => _dialect;

    /// <inheritdoc />
    /// <remarks>
    /// Session-level <c>SET</c>, not the restore's <c>SET LOCAL</c>: a local setting outside a transaction
    /// block is discarded, so the header states both for the whole session and every table block of the
    /// data file opens them again locally, the way the restore's batch does (D2).
    /// </remarks>
    public IReadOnlyList<string> HeaderStatements() =>
    [
        "SET TIME ZONE 'UTC';",
        "SET standard_conforming_strings = 'on';",
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> SchemaStatements(
        SchemaStage stage,
        IReadOnlyList<ISchemaObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);

        return
        [
            .. PostgreSqlWriter
                .BuildStatements(Dialect, stage, objects)
                .SelectMany(ScriptStatementGuard.Guard),
        ];
    }

    /// <inheritdoc />
    /// <remarks>
    /// The block is written as it is built: the banner and the transaction open it, one statement leaves
    /// per batch, and the commit closes it. Only the batch being filled is held, which is the budget the
    /// restore's own data pass uses, so a table larger than memory still writes one statement at a time.
    /// </remarks>
    public async IAsyncEnumerable<string> InsertStatements(
        TableDefinition table,
        IAsyncEnumerable<DataValue[]> rows,
        long rowCount,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rows);

        var targets = PostgreSqlWriter.BuildRowTargets(table.Columns);

        if (targets.Count == 0)
        {
            throw new NotSupportedException(
                $"The catalog records no column of the table \"{table.Name}\" a script can write, so its "
                + "rows cannot be inserted. A statement with an empty column list fails on the server, and "
                + "leaving the rows out would report a file that omits them as complete.");
        }

        var tableName = TableName(table);
        var columns = string.Join(
            ", ",
            targets.Select(t => PgIdentifierQuoter.QuoteIdentifier(t.Column)));
        var arbitrated = HasArbiter(table);

        yield return $"-- {tableName}: {rowCount} row(s).";
        yield return "BEGIN;";
        yield return "SET LOCAL TIME ZONE 'UTC';";
        yield return "SET LOCAL standard_conforming_strings = 'on';";

        var batch = new List<DataValue[]>();
        var length = 0;

        await foreach (var row in rows.WithCancellation(ct))
        {
            // The row's own text plus the "(" that opens it and the "), " that follows it.
            var rowLength =
                PostgreSqlWriter.MeasureRowSqlLength(targets, row, inlinePayloads: true) + 4;

            if (batch.Count > 0
                && (batch.Count >= PostgreSqlWriter.MaxBatchRows
                    || length + rowLength > PostgreSqlWriter.MaxBatchTextLength))
            {
                yield return InsertStatement(tableName, columns, targets, batch, arbitrated);
                batch.Clear();
                length = 0;
            }

            batch.Add(row);
            length += rowLength;
        }

        if (batch.Count > 0)
        {
            yield return InsertStatement(tableName, columns, targets, batch, arbitrated);
        }

        yield return "COMMIT;";
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>VALIDATE CONSTRAINT</c> on a key that is already validated is a no-op, so the tail costs nothing
    /// on a second run; the refresh makes a materialized view's contents match the data the same run wrote.
    /// A sequence is deliberately absent: the catalog records a declaration and never a current value, so
    /// there is no state to restore and nothing is invented (D6).
    /// </remarks>
    public IReadOnlyList<string> TailStatements(
        IReadOnlyList<TableDefinition> tables,
        IReadOnlyList<ViewDefinition> views)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(views);

        var statements = new List<string>();

        foreach (var table in tables)
        {
            var tableName = TableName(table);

            foreach (var foreignKey in table.ForeignKeys)
            {
                statements.Add(
                    $"ALTER TABLE {tableName} VALIDATE CONSTRAINT "
                    + $"{PgIdentifierQuoter.QuoteIdentifier(foreignKey.Name)};");
            }
        }

        foreach (var view in views.Where(v => v.IsMaterialized))
        {
            statements.Add(
                $"REFRESH MATERIALIZED VIEW {TableName(view.SchemaRef, view.Name)};");
        }

        return statements;
    }

    /// <summary>
    /// Builds the statement that carries one batch, in the shape the table's own uniqueness allows.
    /// </summary>
    private static string InsertStatement(
        string tableName,
        string columns,
        IReadOnlyList<PostgreSqlWriter.RowTarget> targets,
        List<DataValue[]> batch,
        bool arbitrated) =>
        arbitrated
            ? ArbitratedInsert(tableName, columns, targets, batch)
            : KeylessInsert(tableName, columns, targets, batch);

    /// <summary>
    /// A table with a conflict arbiter: the restore's own <c>INSERT … VALUES …</c> text with target-less
    /// <c>ON CONFLICT DO NOTHING</c> appended, which covers every arbiter the table has without one being
    /// named.
    /// </summary>
    private static string ArbitratedInsert(
        string tableName,
        string columns,
        IReadOnlyList<PostgreSqlWriter.RowTarget> targets,
        List<DataValue[]> batch)
    {
        var values = PostgreSqlWriter.BuildInsertStatement(
            tableName,
            columns,
            targets,
            batch,
            parameters: null,
            inlinePayloads: true);

        // The builder closes the statement, and the clause belongs before that terminator.
        return string.Concat(values[..^1], " ON CONFLICT DO NOTHING;");
    }

    /// <summary>
    /// A table with no arbiter this file can rely on: the whole row is the identity, because the catalog
    /// records nothing narrower, so the statement inserts only a row the target does not hold yet.
    /// </summary>
    /// <remarks>
    /// The values are written once, into a <c>VALUES</c> list the comparison reads back by name, so a wide
    /// row is not spelled twice in one statement. <c>IS NOT DISTINCT FROM</c> makes a null match a null,
    /// which is the only comparison that gives a keyless row a stable identity. A null carries its column's
    /// type because the list is typed before the target's columns are known: without the cast, a column
    /// whose every value is null would be read as text and compared with a key column of another type.
    /// </remarks>
    private static string KeylessInsert(
        string tableName,
        string columns,
        IReadOnlyList<PostgreSqlWriter.RowTarget> targets,
        List<DataValue[]> batch)
    {
        var quoted = targets.Select(t => PgIdentifierQuoter.QuoteIdentifier(t.Column)).ToList();

        var sql = new StringBuilder();
        sql.Append("INSERT INTO ").Append(tableName).Append(" (").Append(columns).Append(")\n");
        sql.Append("SELECT ").Append(Join(quoted, "v.")).Append('\n');
        sql.Append("FROM (VALUES\n");

        for (var r = 0; r < batch.Count; r++)
        {
            if (r > 0)
            {
                sql.Append(",\n");
            }

            sql.Append("    (");
            PostgreSqlWriter.AppendRowValues(
                sql,
                targets,
                batch[r],
                parameters: null,
                inlinePayloads: true,
                typedNulls: true);
            sql.Append(')');
        }

        sql.Append(") AS v(").Append(columns).Append(")\n");
        sql.Append("WHERE NOT EXISTS (\n");
        sql.Append("    SELECT 1 FROM ").Append(tableName).Append(" AS t\n");
        sql.Append("    WHERE ");
        sql.Append(
            string.Join(
                "\n      AND ",
                quoted.Select(c => $"t.{c} IS NOT DISTINCT FROM v.{c}")));
        sql.Append(");");

        return sql.ToString();
    }

    private static string Join(IReadOnlyList<string> identifiers, string prefix) =>
        string.Join(", ", identifiers.Select(i => $"{prefix}{i}"));

    /// <summary>
    /// Says whether the table has a uniqueness constraint <c>ON CONFLICT DO NOTHING</c> can defer to for
    /// every row the catalog holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A conflict is raised only where the target's uniqueness is real for the row being inserted. A unique
    /// key that reads a null is no key at all — two nulls never equal each other, so a second row carrying
    /// one is accepted — and a partial unique index says nothing about the rows outside its predicate. A
    /// table that can only show one of those takes the keyless form instead, which compares the whole row.
    /// </para>
    /// <para>
    /// The catalog records an index's text but not its column list, so the columns are read from the text
    /// the engine wrote: an index whose list is an expression, or that no captured definition describes,
    /// proves nothing and is passed over.
    /// </para>
    /// </remarks>
    private static bool HasArbiter(TableDefinition table) =>
        table.PrimaryKey is not null
        || table.UniqueConstraints.Any(unique => CoversEveryRow(table, unique.Columns))
        || table.Indexes.Any(index => index.IsUnique && IsFullUniqueIndex(table, index));

    /// <summary>
    /// Says whether every column of a candidate key is declared <c>NOT NULL</c>, which is what makes a
    /// unique key read a value at all. A column the catalog does not describe is no proof either.
    /// </summary>
    private static bool CoversEveryRow(TableDefinition table, IReadOnlyList<string> columns)
    {
        if (columns.Count == 0)
        {
            return false;
        }

        foreach (var name in columns)
        {
            var column = table.Columns.FirstOrDefault(
                candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

            if (column is null || column.Nullable)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Says whether a unique index is unconditional — no <c>WHERE</c> predicate — and keys only plain
    /// columns that are all <c>NOT NULL</c>.
    /// </summary>
    private static bool IsFullUniqueIndex(TableDefinition table, IndexDefinition index)
    {
        var text = index.NativeDefinition?.Text;

        if (string.IsNullOrWhiteSpace(text)
            || text.Contains(" WHERE ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return PlainIndexColumns(text) is { } columns && CoversEveryRow(table, columns);
    }

    /// <summary>
    /// Reads the column list out of a captured <c>CREATE INDEX</c> definition, or answers
    /// <see langword="null"/> when the list is not a plain one.
    /// </summary>
    private static IReadOnlyList<string>? PlainIndexColumns(string definition)
    {
        var open = definition.IndexOf('(');
        var close = open < 0 ? -1 : definition.IndexOf(')', open);

        if (open < 0 || close < 0)
        {
            return null;
        }

        var list = new List<string>();

        foreach (var part in definition[(open + 1)..close].Split(','))
        {
            var name = PlainColumnName(part.Trim());

            if (name is null)
            {
                // An expression, an operator class or a sort clause: the index is not one whose columns
                // this file can read, so it proves no arbiter.
                return null;
            }

            list.Add(name);
        }

        return list;
    }

    /// <summary>Answers one plainly named index column, or <see langword="null"/> for anything else.</summary>
    private static string? PlainColumnName(string part)
    {
        if (part.Length == 0)
        {
            return null;
        }

        if (part[0] == '"')
        {
            if (part.Length < 2 || part[^1] != '"')
            {
                return null;
            }

            var quoted = part[1..^1];

            return quoted.Contains("\"\"", StringComparison.Ordinal)
                ? quoted.Replace("\"\"", "\"", StringComparison.Ordinal)
                : quoted;
        }

        return part.All(c => char.IsLetterOrDigit(c) || c is '_' or '$') ? part : null;
    }

    private static string TableName(TableDefinition table) =>
        TableName(table.SchemaRef, table.Name);

    private static string TableName(ObjectRef schemaRef, string name)
    {
        var schema = schemaRef.ObjectName();

        if (string.IsNullOrWhiteSpace(schema))
        {
            throw new InvalidOperationException(
                $"The catalog being scripted does not name the schema the object \"{name}\" lives in, so "
                + "it cannot be created in a schema chosen by guess.");
        }

        return PgIdentifierQuoter.QuoteSchemaQualified(schema, name);
    }
}
