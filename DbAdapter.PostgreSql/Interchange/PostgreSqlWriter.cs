using System.Text;

using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DbAdapter.Definitions.Interchange;

using DbAdapter.PostgreSql.Ddl;

using DbAdapter.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

using NpgsqlTypes;

using Models = CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Interchange;

/// <summary>
/// PostgreSQL implementation of <see cref="IDatabaseWriter"/>.
/// <para>
/// The writer brings almost no query of its own to the import: the target check reads through
/// <see cref="IMetadataSource"/>, the same role copy and compare use, and the statements that create
/// an object come either from <see cref="PgDdlGenerator"/>, the generator the copy pipeline runs, or
/// from the interchange model itself where the live model has no field for what the catalog keeps.
/// What belongs to the import alone is the order — the stages, and the transaction that makes each
/// stage the commit unit. The two exceptions are the data pass, which builds its own <c>INSERT</c>
/// text from the stored tokens, and validation, which counts the rows a table ended up holding.
/// </para>
/// <para>
/// A stage whose objects this writer cannot build is refused, and so is the security section, which
/// the catalog captures but no stage of this restore applies. Applying nothing and reporting success
/// is the outcome the format's rules name explicitly ("nothing is silently dropped").
/// </para>
/// </summary>
public sealed class PostgreSqlWriter : IDatabaseWriter
{
    /// <summary>
    /// The schema a freshly created PostgreSQL database already holds, so its presence is not the
    /// user content the emptiness check guards against.
    /// </summary>
    private const string DefaultSchema = "public";

    /// <summary>
    /// The extension PostgreSQL installs in every database, excluded for the same reason.
    /// </summary>
    private static readonly string[] BuiltInExtensions = ["plpgsql"];

    /// <summary>
    /// The rows one <c>INSERT</c> statement carries. A batch is the unit the data pass commits, so this
    /// also says how much a failing batch writes back.
    /// </summary>
    internal const int MaxBatchRows = 1000;

    /// <summary>
    /// The text length at which a batch is closed whatever it holds. A row of large text or JSON values
    /// outruns the row count, and a statement of several megabytes is not worth building to send.
    /// </summary>
    internal const int MaxBatchTextLength = 2_000_000;

    /// <summary>
    /// What one stored binary value costs the batch budget: its placeholder text. The bytes themselves travel
    /// as parameter data, outside the statement text R7 measures, so a payload never inflates the budget.
    /// </summary>
    private const int BinaryPlaceholderTextLength = 12;

    private static readonly PgDdlGenerator Ddl = new();

    private readonly ILogger<PostgreSqlWriter> _logger;
    private readonly DatabaseTarget _target;
    private readonly ImportOptions _options;
    private readonly IMetadataSource _metadataSource;
    private NpgsqlConnection? _connection;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlWriter"/> class.
    /// </summary>
    public PostgreSqlWriter(
        IDatabaseAdapter adapter,
        DatabaseTarget target,
        ImportOptions options,
        ILogger<PostgreSqlWriter> logger,
        IMetadataSource metadataSource)
    {
        Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metadataSource = metadataSource ?? throw new ArgumentNullException(nameof(metadataSource));
    }

    /// <inheritdoc />
    public IDatabaseAdapter Adapter { get; }

    /// <inheritdoc />
    public ICapabilityDetector CapabilityDetector => throw new NotSupportedException(
        "Capability detector requires an active connection. Use the copy pipeline for live operations.");

    /// <inheritdoc />
    public async Task EnsureEmptyTargetAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _logger.LogInformation("Ensuring target database is empty");

        var model = await _metadataSource.ReadDatabaseModelAsync(excludePlatformSchemas: true, ct);
        var findings = DescribeUserContent(model);

        if (findings.Count > 0)
        {
            throw new InvalidOperationException(
                $"The target database \"{model.DatabaseName}\" is not empty: it already holds " +
                $"{string.Join(", ", findings)}. A restore applies its catalog to an empty " +
                "database, and cleaning the target is a separate act performed beforehand.");
        }
    }

    /// <inheritdoc />
    public async Task ApplySchemaAsync(
        SchemaStage stage,
        IReadOnlyList<ISchemaObject> objects,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(objects);

        if (objects.Count == 0)
        {
            return;
        }

        var statements = BuildStatements(Adapter.Info.Dialect, stage, objects);

        if (statements.Count == 0)
        {
            // A stage whose objects need no statement — tables that carry no foreign key, indexes all
            // of which a constraint already made — states the same fact about the source that an empty
            // stage does, so it opens no transaction rather than committing nothing.
            return;
        }

        _logger.LogInformation(
            "Applying schema stage {Stage}: {StatementCount} statement(s)",
            stage,
            statements.Count);

        var connection = await EnsureConnectionAsync(ct);

        // One transaction per stage: a stage is the commit unit the format defines, so a failure
        // rolls back this stage and leaves the earlier ones in place, described in the result.
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var statement in statements)
            {
                await using var command = new NpgsqlCommand(statement, connection, transaction);
                await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<long> WriteTableRowsAsync(
        TableRef table,
        IReadOnlyList<ColumnDefinition> columns,
        IAsyncEnumerable<DataValue[]> rows,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        var targets = BuildRowTargets(columns);

        if (targets.Count == 0)
        {
            throw new NotSupportedException(
                $"The catalog records no column of {QualifiedName(table)} that a restore can write, " +
                "so its rows cannot be inserted. A statement with an empty column list fails on the " +
                "server, and returning the rows as applied would not be true.");
        }

        var columnList = string.Join(
            ", ",
            targets.Select(t => PgIdentifierQuoter.QuoteIdentifier(t.Column)));
        var tableName = QuoteTableName(table);
        var connection = await EnsureConnectionAsync(ct);

        var applied = 0L;
        var batch = new List<DataValue[]>();

        // R7: the batch budget is measured on the final SQL text that goes on the wire — after
        // quoting, escaping and joining — not on raw value lengths before those transformations.
        // Each row's SQL text is built once and measured; when adding it would push the batch past
        // the budget, the current batch is sent first.
        await foreach (var row in rows.WithCancellation(ct))
        {
            var rowSqlLength = MeasureRowSqlLength(targets, row);

            // When the batch is non-empty and adding this row would exceed the text budget,
            // send what is already accumulated first.
            if (batch.Count > 0 && batch.Count < MaxBatchRows)
            {
                var projectedLength = EstimateBatchStatementLength(tableName, columnList, targets, batch)
                    + rowSqlLength + 2; // ", " separator before the new row's values
                if (projectedLength > MaxBatchTextLength)
                {
                    applied += await InsertBatchAsync(connection, tableName, columnList, targets, batch, ct);
                    batch.Clear();
                }
            }

            batch.Add(row);

            // One row always forms a batch on its own, however large it is: a single value bigger
            // than the text budget is the source's own row, and there is no smaller batch to send.
            if (batch.Count >= MaxBatchRows)
            {
                applied += await InsertBatchAsync(connection, tableName, columnList, targets, batch, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            applied += await InsertBatchAsync(connection, tableName, columnList, targets, batch, ct);
        }

        _logger.LogInformation(
            "Applied {RowCount} row(s) to {Table}",
            applied,
            QualifiedName(table));

        return applied;
    }

    /// <summary>
    /// Estimates the SQL text length a single row's values will occupy in an INSERT statement,
    /// measured after quoting and escaping — the budget R7 names.
    /// </summary>
    /// <remarks>
    /// A payload costs its placeholder when the bytes travel as parameter data, and its whole base64
    /// text when <paramref name="inlinePayloads"/> writes them into the statement, which is the script
    /// renderer's case: a budget that counted a 12-character placeholder for a megabyte of bytes would
    /// let a single statement grow past the limit it exists to hold.
    /// </remarks>
    internal static int MeasureRowSqlLength(
        IReadOnlyList<RowTarget> targets,
        DataValue[] row,
        bool inlinePayloads = false)
    {
        var length = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            if (i > 0)
            {
                length += 2; // ", "
            }

            var target = targets[i];
            if (target.RowIndex >= row.Length)
            {
                continue;
            }

            var value = row[target.RowIndex];
            if (value.Binary is { } payload)
            {
                length += inlinePayloads
                    ? InlinePayloadTextLength(payload, target.Type)
                    : BinaryPlaceholderTextLength; // "@blobN", the bytes going as parameter data
            }
            else if (value.IsNull || value.Value is null)
            {
                length += 4; // "NULL"
            }
            else
            {
                // The escaped value: each single quote becomes two, plus the surrounding quotes
                // and the ::type cast.
                var escaped = value.Value.Replace("'", "''");
                length += 1 + escaped.Length + 1 + 2 + target.Type.Length; // 'value'::type
            }
        }

        return length;
    }

    /// <summary>
    /// The length of the text an inlined payload occupies: <c>decode('&lt;base64&gt;', 'base64')::type</c>.
    /// </summary>
    private static int InlinePayloadTextLength(IBinaryPayload payload, string type)
    {
        // Base64 turns every three bytes into four characters, padded to a whole group.
        var base64 = 4 * ((payload.Length + 2) / 3);

        return "decode('".Length + (int)base64 + "', 'base64')::".Length + type.Length;
    }

    /// <summary>
    /// Builds the INSERT statement for the current batch rows and returns its text length.
    /// Used to project whether adding another row would exceed the text budget.
    /// </summary>
    internal static int EstimateBatchStatementLength(
        string tableName,
        string columnList,
        IReadOnlyList<RowTarget> targets,
        List<DataValue[]> batch)
    {
        // "INSERT INTO <table> (<cols>) VALUES " = prefix
        var prefix = $"INSERT INTO {tableName} ({columnList}) VALUES ".Length;
        var suffix = 1; // ";"
        var separators = batch.Count > 1 ? (batch.Count - 1) * 2 : 0; // ", " between rows

        var rowsLength = 0;
        foreach (var row in batch)
        {
            rowsLength += MeasureRowSqlLength(targets, row) + 2; // "(" + ")" per row
        }

        return prefix + rowsLength + separators + suffix;
    }

    /// <inheritdoc />
    public Task ApplySecurityAsync(
        ESecurityStage stage,
        SecuritySection security,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(security);

        if (security.Roles.Count == 0 && security.Grants.Count == 0 && security.Policies.Count == 0)
        {
            return Task.CompletedTask;
        }

        throw new NotSupportedException(
            $"The security stage \"{stage}\" holds objects this adapter cannot apply yet, so " +
            "nothing was executed.");
    }

    /// <inheritdoc />
    public async Task<ImportValidationReport> ValidateAsync(
        TargetExpectation expectation,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(expectation);

        // The target is counted, not assumed: the object counts come from the same metadata role the
        // emptiness check uses, and the row counts from the tables themselves. What the expectation
        // asks for that this cannot answer is reported as unchecked rather than left out of the
        // report, because a green summary that stands for a check that never ran is the failure the
        // format's rules name.
        var model = await _metadataSource.ReadDatabaseModelAsync(excludePlatformSchemas: true, ct);
        var countedObjects = CountTargetObjects(model, expectation.SchemaNames);
        var issues = new List<InterchangeIssue>();
        var foundObjectCounts = new Dictionary<EObjectKind, int>();
        var foundRowCounts = new Dictionary<TableRef, long>();

        foreach (var (kind, expected) in expectation.ObjectCounts)
        {
            if (!countedObjects.TryGetValue(kind, out var found))
            {
                issues.Add(new InterchangeIssue(
                    "ValidationKindNotChecked",
                    EIssueSeverity.Warning,
                    $"The catalog records {expected} object(s) of kind {kind}, but the live model " +
                    "keeps no collection that kind can be counted from, so the target was not " +
                    "checked for them."));
                continue;
            }

            foundObjectCounts[kind] = found;

            if (found == expected)
            {
                continue;
            }

            issues.Add(new InterchangeIssue(
                "ObjectCountMismatch",
                EIssueSeverity.Error,
                $"The catalog records {expected} object(s) of kind {kind}, but the target holds " +
                $"{found}."));
        }

        if (expectation.TableRowCounts.Count > 0)
        {
            var connection = await EnsureConnectionAsync(ct);

            foreach (var (table, expected) in expectation.TableRowCounts)
            {
                var found = await ReadTableRowCountAsync(connection, table, ct);
                foundRowCounts[table] = found;

                if (found == expected)
                {
                    continue;
                }

                issues.Add(new InterchangeIssue(
                    "RowCountMismatch",
                    EIssueSeverity.Error,
                    $"The catalog records {expected} row(s) for {QualifiedName(table)}, but the " +
                    $"target holds {found}."));
            }
        }

        return new ImportValidationReport
        {
            Passed = issues.All(i => i.Severity != EIssueSeverity.Error),
            Issues = issues,
            FoundObjectCounts = foundObjectCounts,
            FoundRowCounts = foundRowCounts,
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    /// <summary>
    /// Builds the statements that create one stage's objects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A schema is named by its own name; every other object names a parent — the schema a sequence
    /// lives in, the table an index belongs to — and the interchange model points at that parent by
    /// <see cref="ObjectRef"/> qualified-name references. An object whose parent reference does not
    /// resolve to a name stops the import: creating a sequence in the default schema because its schema
    /// reference went unanswered would relocate the object and still report the stage as applied.
    /// </para>
    /// <para>
    /// Schemas, sequences and views are converted into the live model records
    /// <see cref="PgDdlGenerator"/> works over, so a restore builds them with the statements the copy
    /// pipeline already uses. Routines need no generator: <c>pg_get_functiondef</c> handed the catalog
    /// a complete <c>CREATE</c> statement, and rebuilding one from the parsed signature would be a
    /// rewrite of text that is already exact. Tables, and the foreign keys that hang off them, are
    /// written here from the interchange model instead, because the live model that generator works
    /// over has no field for a canonical column's collation and no place for an exclusion constraint:
    /// converting would ask the format to lose what it keeps. An index needs neither helper, because
    /// the catalog holds its complete <c>CREATE</c> text; what the index stage decides is which of the
    /// recorded indexes to leave alone. A trigger needs the same text for the stronger reason that its
    /// structured fields describe less than half of it. A type — an enum, a composite, a domain — has no
    /// structured home in the format at all, so the statement in the catalog's native file is the only
    /// record of it. That file carries more than types, and the objects it holds are routed to the stage
    /// that creates each of them before any of them is applied, so a stage is never handed an object it
    /// has nothing to do with.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> BuildStatements(
        string dialect,
        SchemaStage stage,
        IReadOnlyList<ISchemaObject> objects) =>
        stage switch
        {
            SchemaStage.Schemas => CreateSchemaStatements(objects),
            SchemaStage.Types => CreateTypeStatements(dialect, objects),
            SchemaStage.Sequences => CreateSequenceStatements(objects),
            SchemaStage.Tables => CreateTableStatements(dialect, objects),
            SchemaStage.Routines => CreateRoutineStatements(dialect, objects),
            SchemaStage.Views => CreateViewStatements(dialect, objects),
            SchemaStage.Constraints => CreateForeignKeyStatements(objects),
            SchemaStage.Indexes => CreateIndexStatements(dialect, objects),
            SchemaStage.Triggers => CreateTriggerStatements(dialect, objects),
            _ => throw new NotSupportedException(
                $"The schema stage \"{stage}\" holds {objects.Count} object(s) this adapter " +
                "cannot apply yet, so nothing was executed."),
        };

    /// <summary>
    /// Names the user content a target already holds, most important first.
    /// </summary>
    /// <remarks>
    /// System and platform schemas are not in the model at all — the check asks the metadata role to
    /// exclude them, the way the copy pipeline does — so a platform that pre-creates its own schemas
    /// is not read as a target holding user content. <c>public</c> and <c>plpgsql</c> are set aside
    /// for the same reason: every database PostgreSQL creates has both, so counting them would
    /// refuse every restore.
    /// </remarks>
    internal static IReadOnlyList<string> DescribeUserContent(Models.DatabaseModel model)
    {
        var findings = new List<(int Count, string Noun)>
        {
            (model.Schemas.Count(
                 s => !s.IsSystem
                      && !string.Equals(s.Name, DefaultSchema, StringComparison.Ordinal)),
             "schema"),
            (model.Tables.Count, "table"),
            (model.Views.Count, "view"),
            (model.MaterializedViews.Count, "materialized view"),
            (model.Sequences.Count, "sequence"),
            (model.Enums.Count, "enum type"),
            (model.Domains.Count, "domain"),
            (model.CompositeTypes.Count, "composite type"),
            (model.Functions.Count, "routine"),
            (model.Triggers.Count, "trigger"),
            (model.Policies.Count, "row-level security policy"),
            (model.Publications.Count, "publication"),
            (model.Subscriptions.Count, "subscription"),
            (model.Extensions.Count(
                 e => !BuiltInExtensions.Contains(e.Name, StringComparer.OrdinalIgnoreCase)),
             "extension"),
        };

        return [.. findings.Where(f => f.Count > 0).Select(f => $"{f.Count} {Pluralize(f.Noun, f.Count)}")];
    }

    private static IReadOnlyList<string> CreateSchemaStatements(IReadOnlyList<ISchemaObject> objects)
    {
        var schemas = new List<Models.SchemaDefinition>(objects.Count);

        foreach (var obj in objects)
        {
            if (obj is not SchemaDefinition schema)
            {
                throw WrongObject(SchemaStage.Schemas, nameof(SchemaDefinition), obj);
            }

            // A name the server owns is never created. PostgreSQL rejects the name itself — 42939
            // "unacceptable schema name" — whatever IF NOT EXISTS says, and every target the server
            // bootstrapped already holds the schema. A catalog captured before the export stopped
            // recording system schemas still names them, so they are skipped here rather than
            // refusing the whole restore.
            if (PgSystemSchemas.AllSet.Contains(schema.Name))
            {
                continue;
            }

            // Ownership is not part of this statement. The import assigns owners in its own security
            // stage, once the roles exist, so the schema is created owned by the connecting role.
            schemas.Add(new Models.SchemaDefinition(schema.Name, Owner: string.Empty));
        }

        return Ddl.GenerateCreateSchemas(schemas);
    }

    /// <summary>
    /// Creates the types the catalog's native file recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These objects come from <c>native/&lt;dialect&gt;.json</c>, the file the capture uses for what the
    /// structured sections have no home for. The text is the record: the format gives a domain no base
    /// type, an enum no labels and a composite no attributes, so a statement written here would have to
    /// invent the fields it is being asked to write from. What the capture assembled is run instead,
    /// after the two checks every verbatim stage makes — the dialect the text is written in, and whether
    /// it is a whole statement.
    /// </para>
    /// <para>
    /// Only the kinds the format routes to this stage arrive here, and that is <see
    /// cref="NativeObjectRouting"/>'s decision rather than this handler's: the importer groups the file by
    /// that table and asks the adapter whether it creates each kind, so an object nothing can create stops
    /// the import before the first write. An object of another kind reaching this stage therefore means a
    /// caller that misread the table, which is a programming error and not a fact about the catalog, and it
    /// is reported as one.
    /// </para>
    /// <para>
    /// Order is by kind, from the same table: an enum, then a composite, then a range, then a domain,
    /// because a composite can hold an enum's column and a domain can rest on either. That is the only
    /// order the file can answer, since what its objects name are names and not ids the import could
    /// follow. A source whose types need another order is stopped by the server, and the stage's
    /// transaction rolls back instead of leaving half a type behind.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> CreateTypeStatements(
        string dialect,
        IReadOnlyList<ISchemaObject> objects)
    {
        var staged = new List<(int Rank, NativeObjectDefinition Type)>(objects.Count);

        foreach (var obj in objects)
        {
            if (obj is not NativeObjectDefinition native)
            {
                throw WrongObject(SchemaStage.Types, nameof(NativeObjectDefinition), obj);
            }

            var stage = NativeObjectRouting.StageFor(native.Kind);
            if (stage != SchemaStage.Types)
            {
                throw new ArgumentException(
                    $"The {SchemaStage.Types} stage was handed the {native.Kind} \"{native.Name}\", "
                    + (stage is { } other
                        ? $"which the routing places in the {other} stage."
                        : ", a kind no stage creates from the native file."),
                    nameof(objects));
            }

            staged.Add((NativeObjectRouting.RankInStage(native.Kind), native));
        }

        return [
            .. staged
                .OrderBy(s => s.Rank)
                .ThenBy(s => s.Type.SchemaName, StringComparer.Ordinal)
                .ThenBy(s => s.Type.Name, StringComparer.Ordinal)
                .Select(s => CreateTypeStatement(dialect, s.Type)),
        ];
    }

    private static string CreateTypeStatement(string dialect, NativeObjectDefinition native)
    {
        var named = $"The type \"{native.Name}\"";
        var text = native.NativeDefinition;

        if (text is null || string.IsNullOrWhiteSpace(text.Text))
        {
            throw new InvalidOperationException(
                $"{named} carries no recorded statement. The format keeps no fields for a type's " +
                "labels, attributes or base type, so there is nothing here to write a statement from.");
        }

        if (!text.Complete)
        {
            throw new InvalidOperationException(
                $"{named} is recorded as a fragment rather than a complete statement, so running it " +
                "would create something other than the source's type.");
        }

        RequireDialect(dialect, text.Dialect, named);

        return $"{text.Text.TrimEnd(';')};";
    }

    /// <summary>
    /// Creates the sequences the catalog recorded, in the schemas it says they live in.
    /// </summary>
    /// <remarks>
    /// The catalog holds a sequence's options and nothing else about it: the live model's ownership
    /// link (<c>OWNED BY</c>, identity backing) has no field in the format yet, so an owned sequence
    /// is restored as a standalone one. The reader marks that loss in the object's notes.
    /// </remarks>
    private static IReadOnlyList<string> CreateSequenceStatements(
        IReadOnlyList<ISchemaObject> objects)
    {
        var sequences = new List<Models.SequenceDefinition>(objects.Count);

        foreach (var obj in objects)
        {
            if (obj is not SequenceDefinition sequence)
            {
                throw WrongObject(SchemaStage.Sequences, nameof(SequenceDefinition), obj);
            }

            sequences.Add(new Models.SequenceDefinition(
                RequireSchema(sequence.SchemaRef, $"the sequence \"{sequence.Name}\""),
                sequence.Name,
                sequence.StartValue,
                sequence.Increment,
                sequence.MinValue,
                sequence.MaxValue,
                sequence.Cache,
                sequence.Cycle,
                sequence.DataType,
                Comment: null));
        }

        return Ddl.GenerateCreateSequences(sequences);
    }

    /// <summary>
    /// Creates the tables the catalog recorded, every parent before the partitions that hang off it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A partition declares no columns of its own — PostgreSQL gives it the parent's — so it is created
    /// as <c>PARTITION OF</c> its parent with nothing but its bound, and the parent has to exist
    /// first. Multi-level partitioning needs the same rule again, since a partition can itself be
    /// partitioned, so the statements are emitted in rounds: each round takes the tables whose parent
    /// is already created, or that have no parent at all.
    /// </para>
    /// <para>
    /// Foreign keys are not here. The order the source created its tables in cannot be relied on to
    /// have created a referenced table before the referencing one, so the keys belong to the
    /// <see cref="SchemaStage.Constraints"/> stage, which runs once every table of the catalog exists.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> CreateTableStatements(
        string dialect,
        IReadOnlyList<ISchemaObject> objects)
    {
        var tables = new List<TableDefinition>(objects.Count);

        foreach (var obj in objects)
        {
            if (obj is not TableDefinition table)
            {
                throw WrongObject(SchemaStage.Tables, nameof(TableDefinition), obj);
            }

            tables.Add(table);
        }

        var statements = new List<string>(tables.Count);
        var created = new HashSet<ObjectRef>();
        var pending = tables;

        // A parent the catalog does not name is asked about before the ordering below, because it is
        // a fact about the catalog rather than about the set handed to this stage, and "the reference
        // is empty" is the answer the import has to be given.
        foreach (var table in tables)
        {
            if (table.ParentRef is { } linked)
            {
                RequireTableName(linked, $"\"{table.Name}\"");
            }
        }

        while (pending.Count > 0)
        {
            var deferred = new List<TableDefinition>();

            foreach (var table in pending)
            {
                if (table.ParentRef is null || created.Contains(table.ParentRef.Value))
                {
                    statements.Add(CreateTableStatement(dialect, table));
                    created.Add(table.Ref);
                }
                else
                {
                    deferred.Add(table);
                }
            }

            if (deferred.Count == pending.Count)
            {
                // Nothing in the set can move, because a parent link leads to a table this stage was
                // not given, or around a cycle. Either way the order the statements need does not
                // exist, and emitting them in any other order would fail on the server one table at a
                // time with a message about a missing relation.
                throw new InvalidOperationException(
                    $"The {SchemaStage.Tables} stage holds {deferred.Count} table(s) whose partition " +
                    "parent is not among the tables it can create first, so no statement order " +
                    "would run.");
            }

            pending = deferred;
        }

        return statements;
    }

    /// <summary>
    /// Builds the one statement that creates a table, in whichever of its three shapes the source
    /// left it: a table on its own, a partitioned table waiting for its partitions, or a partition of
    /// a parent that is already created.
    /// </summary>
    private static string CreateTableStatement(string dialect, TableDefinition table)
    {
        var named = $"the table \"{table.Name}\"";
        var tableName = PgIdentifierQuoter.QuoteSchemaQualified(
            RequireSchema(table.SchemaRef, named),
            table.Name);

        if (table.TableKind is ETableKind.Foreign or ETableKind.Temporary)
        {
            // The kind is recorded and nothing else that shape needs — no server and options for a
            // foreign table, no ON COMMIT clause for a temporary one — so neither is created as a
            // plain table instead.
            throw new InvalidOperationException(
                $"Table \"{table.Name}\" is recorded as a {table.TableKind} table, and the catalog " +
                "keeps none of the details that kind needs, so it is not created.");
        }

        if (table.ExclusionConstraints.Count > 0)
        {
            var recorded = string.Join(
                ", ",
                table.ExclusionConstraints.Select(c => $"\"{c.Name}\""));

            // An exclusion constraint is kept as an object with a name: the format gives it no
            // operator, operator class or predicate to restore from. A table created without it would
            // hold rows the source refused, and be reported as restored.
            throw new InvalidOperationException(
                $"Table \"{table.Name}\" carries exclusion constraint(s) {recorded}, whose definition " +
                "the catalog does not record, so the table is not created.");
        }

        if (table.ParentRef is { } parentId)
        {
            var parent = RequireTableName(parentId, $"\"{table.Name}\"");

            if (string.IsNullOrWhiteSpace(table.PartitionBound))
            {
                // A child with a parent and no bound is an INHERITS child, which PostgreSQL builds
                // from the columns the child declares for itself. The catalog keeps a child's whole
                // column list and no mark of which columns are its own, so that list cannot be
                // written here. Partitions, which declare no columns, are unaffected.
                throw new InvalidOperationException(
                    $"Table \"{table.Name}\" inherits from {parent}, and the catalog does not record " +
                    "which of its columns are its own rather than inherited, so the child is not " +
                    "created.");
            }

            var partition = new StringBuilder();
            partition.Append($"CREATE TABLE {tableName} PARTITION OF {parent} ");
            partition.Append(table.PartitionBound.Trim());
            AppendPartitionBy(partition, table);

            return partition.Append(';').ToString();
        }

        if (table.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Table \"{table.Name}\" is recorded with no columns, which is not a table " +
                "PostgreSQL can create.");
        }

        var declarations = new List<string>(table.Columns.Count + 1);

        foreach (var column in table.Columns)
        {
            declarations.Add(CreateColumnDeclaration(dialect, column));
        }

        if (table.PrimaryKey is { } primaryKey)
        {
            declarations.Add(
                $"CONSTRAINT {PgIdentifierQuoter.QuoteIdentifier(primaryKey.Name)} PRIMARY KEY " +
                $"({JoinIdentifiers(primaryKey.Columns)})");
        }

        foreach (var unique in table.UniqueConstraints)
        {
            declarations.Add(
                $"CONSTRAINT {PgIdentifierQuoter.QuoteIdentifier(unique.Name)} UNIQUE " +
                $"({JoinIdentifiers(unique.Columns)})");
        }

        foreach (var check in table.CheckConstraints)
        {
            var expression = check.Expression;

            if (string.IsNullOrWhiteSpace(expression.Text))
            {
                throw new InvalidOperationException(
                    $"Check constraint \"{check.Name}\" on table \"{table.Name}\" carries no " +
                    "expression, so it cannot be created.");
            }

            RequireDialect(dialect, expression.Dialect, $"The check constraint \"{check.Name}\"");

            // pg_get_constraintdef appends " NOT VALID" for a constraint the source never validated,
            // and that clause is legal only in ALTER TABLE ADD CONSTRAINT.
            declarations.Add(
                $"CONSTRAINT {PgIdentifierQuoter.QuoteIdentifier(check.Name)} " +
                StripNotValid(expression.Text));
        }

        var create = new StringBuilder();
        create.Append($"CREATE TABLE {tableName} (");
        create.Append(string.Join($",{Environment.NewLine}    ", declarations));
        create.Append(')');
        AppendPartitionBy(create, table);

        return create.Append(';').ToString();
    }

    /// <summary>
    /// Builds one column's declaration: its type as the source named it, then the clauses the catalog
    /// has content for.
    /// </summary>
    private static string CreateColumnDeclaration(string dialect, ColumnDefinition column)
    {
        var sql = new StringBuilder();
        sql.Append($"{PgIdentifierQuoter.QuoteIdentifier(column.Name)} {column.NativeType.Name}");

        if (!string.IsNullOrWhiteSpace(column.Collation))
        {
            sql.Append($" COLLATE {PgIdentifierQuoter.QuoteIdentifier(column.Collation)}");
        }

        if (column.Generated is { } generated)
        {
            RequireDialect(
                dialect,
                generated.Dialect,
                $"The generation expression of column \"{column.Name}\"");
            sql.Append($" GENERATED ALWAYS AS ({generated.Text}) STORED");
        }
        else if (!string.IsNullOrWhiteSpace(column.Identity))
        {
            // The catalog records that the column is an identity, not which flavor the source
            // declared, and no stage of this product knows the difference: the metadata reader asks
            // PostgreSQL whether a column is an identity and keeps the yes. BY DEFAULT is the clause
            // the copy pipeline emits, so an ALWAYS column comes back accepting values it would have
            // refused.
            sql.Append(" GENERATED BY DEFAULT AS IDENTITY");
        }
        else if (column.DefaultValue is { } defaultValue
                 && !string.IsNullOrWhiteSpace(defaultValue.Text))
        {
            RequireDialect(dialect, defaultValue.Dialect, $"The default of column \"{column.Name}\"");
            sql.Append($" DEFAULT {defaultValue.Text}");
        }

        if (!column.Nullable)
        {
            sql.Append(" NOT NULL");
        }

        return sql.ToString();
    }

    /// <summary>
    /// Adds the foreign keys the catalog recorded, once every table they point between exists.
    /// </summary>
    /// <remarks>
    /// The keys travel with the table objects, because that is where the format keeps them: this
    /// stage is handed the tables again rather than a constraint section of its own. A key names the
    /// table it points at by qualified-name reference.
    /// </remarks>
    private static IReadOnlyList<string> CreateForeignKeyStatements(
        IReadOnlyList<ISchemaObject> objects)
    {
        var statements = new List<string>();

        foreach (var obj in objects)
        {
            if (obj is not TableDefinition table)
            {
                throw WrongObject(SchemaStage.Constraints, nameof(TableDefinition), obj);
            }

            foreach (var foreignKey in table.ForeignKeys)
            {
                statements.Add(CreateForeignKeyStatement(table, foreignKey));
            }
        }

        return statements;
    }

    private static string CreateForeignKeyStatement(
        TableDefinition table,
        ForeignKeyDefinition foreignKey)
    {
        var schemaName = RequireSchema(table.SchemaRef, $"the table \"{table.Name}\"");
        var referenced = RequireTableName(
            foreignKey.ReferencedTableRef,
            $"\"{foreignKey.Name}\"");

        var sql = new StringBuilder();
        sql.Append($"ALTER TABLE {PgIdentifierQuoter.QuoteSchemaQualified(schemaName, table.Name)}");
        sql.Append($" ADD CONSTRAINT {PgIdentifierQuoter.QuoteIdentifier(foreignKey.Name)}");
        sql.Append($" FOREIGN KEY ({JoinIdentifiers(foreignKey.Columns)})");
        sql.Append($" REFERENCES {referenced} ({JoinIdentifiers(foreignKey.ReferencedColumns)})");

        // The rules are the words the source's system catalogs report, in the clause order
        // PostgreSQL itself prints: update first, then delete. A rule the source left at its default
        // is recorded empty and is not restated.
        if (!string.IsNullOrWhiteSpace(foreignKey.OnUpdate))
        {
            sql.Append($" ON UPDATE {foreignKey.OnUpdate}");
        }

        if (!string.IsNullOrWhiteSpace(foreignKey.OnDelete))
        {
            sql.Append($" ON DELETE {foreignKey.OnDelete}");
        }

        if (foreignKey.IsDeferrable)
        {
            sql.Append(" DEFERRABLE");

            if (foreignKey.IsInitiallyDeferred)
            {
                sql.Append(" INITIALLY DEFERRED");
            }
        }

        return sql.Append(';').ToString();
    }

    /// <summary>
    /// Creates the indexes the catalog recorded, leaving alone the ones a constraint already made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stage is handed the tables, as <see cref="SchemaStage.Constraints"/> is, because whether an
    /// index still has to be created is a question about its table's constraints. PostgreSQL names the
    /// index that backs a primary key or a unique constraint after the constraint and reports that
    /// index in the same list it reports a hand-made one in, so the catalog holds the object twice:
    /// once as the constraint the table statement creates inline, once as an index. Running the second
    /// copy fails with "relation already exists" and takes the stage down with it.
    /// </para>
    /// <para>
    /// What is left runs as the captured <c>pg_get_indexdef</c> text, the source the copy pipeline uses
    /// for the same object, because an index's operator class, sort order, collation, <c>INCLUDE</c>
    /// list and predicate are recorded nowhere else — the format gives an index a name, a table and one
    /// flag. An index with no captured text therefore cannot be rebuilt.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> CreateIndexStatements(
        string dialect,
        IReadOnlyList<ISchemaObject> objects)
    {
        var statements = new List<string>();

        foreach (var obj in objects)
        {
            if (obj is not TableDefinition table)
            {
                throw WrongObject(SchemaStage.Indexes, nameof(TableDefinition), obj);
            }

            // The recorded text already names the schema-qualified table, so the name is not needed to
            // build the statement — but a table whose schema the import cannot name belongs to a
            // catalog that does not describe its own contents, and that is worth stopping for.
            RequireSchema(table.SchemaRef, $"the table \"{table.Name}\"");

            foreach (var index in table.Indexes)
            {
                if (!IsConstraintBacked(table, index.Name))
                {
                    statements.Add(CreateIndexStatement(dialect, table, index));
                }
            }
        }

        return statements;
    }

    /// <summary>
    /// Says whether one of this table's own constraints is what created the index of that name.
    /// </summary>
    /// <remarks>
    /// A constraint and the index enforcing it are one name in the engine's catalogs, so this compares
    /// against the names the table itself records rather than against anything the index carries — the
    /// format gives an index no field saying it came from a constraint.
    /// </remarks>
    private static bool IsConstraintBacked(TableDefinition table, string indexName) =>
        (table.PrimaryKey is { } primaryKey
            && string.Equals(primaryKey.Name, indexName, StringComparison.Ordinal))
        || table.UniqueConstraints.Any(u => string.Equals(u.Name, indexName, StringComparison.Ordinal))
        || table.ExclusionConstraints.Any(e => string.Equals(e.Name, indexName, StringComparison.Ordinal));

    private static string CreateIndexStatement(
        string dialect,
        TableDefinition table,
        IndexDefinition index)
    {
        var named = $"The index \"{index.Name}\" on table \"{table.Name}\"";
        var native = index.NativeDefinition;

        if (native is null || string.IsNullOrWhiteSpace(native.Text))
        {
            throw new InvalidOperationException(
                $"{named} carries no captured definition. The format records an index's name and " +
                "whether it is unique and nothing else, so the statement cannot be written from what " +
                "is here.");
        }

        if (!native.Complete)
        {
            throw new InvalidOperationException(
                $"{named} is recorded as a fragment rather than a complete statement, so running it " +
                "would create something other than the source's index.");
        }

        RequireDialect(dialect, native.Dialect, named);

        return $"{native.Text.TrimEnd(';')};";
    }

    /// <summary>
    /// Builds the statements that create a stage's triggers, and the one that switches off a trigger the
    /// source had switched off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Triggers arrive as themselves rather than inside the tables that hold them, which is not what the
    /// constraint and index stages get: a recorded trigger carries everything its statement needs within
    /// itself, so nothing has to be read out of a sibling. The table is still asked for, both because a
    /// disabled trigger has to be switched off on the table the target made and because a trigger pointing
    /// at a table the catalog does not contain describes a database that never existed.
    /// </para>
    /// <para>
    /// The statement is the captured <c>pg_get_triggerdef</c> text, which the copy pipeline uses for the
    /// same object. The structured fields cannot replace it: a timing, an event list and an orientation
    /// name no function to call, no arguments to pass it, no columns for an <c>UPDATE OF</c> event and no
    /// deferrability, so a statement written from them alone would fire on more than the source fired on.
    /// The <c>WHEN</c> clause survives in this text as well — the live model the catalog is built from
    /// records no condition at all.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> CreateTriggerStatements(
        string dialect,
        IReadOnlyList<ISchemaObject> objects)
    {
        var statements = new List<string>();

        foreach (var obj in objects)
        {
            if (obj is not TriggerDefinition trigger)
            {
                throw WrongObject(SchemaStage.Triggers, nameof(TriggerDefinition), obj);
            }

            var named = $"the trigger \"{trigger.Name}\"";
            var tableName = RequireTableName(trigger.TableRef, named);

            // A captured CREATE TRIGGER always creates an enabled trigger, and the format gives this flag
            // only the enabled and disabled states to hold. Anything else is a state the restore has no
            // statement for, and creating the trigger anyway would report it in a condition it was not in.
            if (trigger.EnabledState is not ("O" or "D"))
            {
                throw new InvalidOperationException(
                    $"The trigger \"{trigger.Name}\" is recorded with the enabled state " +
                    $"\"{trigger.EnabledState}\", which means neither enabled nor disabled to this " +
                    "adapter, so it cannot be restored in the state the source had it in.");
            }

            statements.Add(CreateTriggerStatement(dialect, trigger));

            if (trigger.EnabledState == "D")
            {
                statements.Add(
                    $"ALTER TABLE {tableName} DISABLE TRIGGER " +
                    $"{PgIdentifierQuoter.QuoteIdentifier(trigger.Name)};");
            }
        }

        return statements;
    }

    private static string CreateTriggerStatement(string dialect, TriggerDefinition trigger)
    {
        var named = $"The trigger \"{trigger.Name}\"";
        var native = trigger.NativeDefinition;

        if (native is null || string.IsNullOrWhiteSpace(native.Text))
        {
            throw new InvalidOperationException(
                $"{named} carries no captured definition. What its record holds instead — a timing, an " +
                "event list and an orientation — is not enough to write the statement, because none of it "
                + "names the function the trigger calls.");
        }

        if (!native.Complete)
        {
            throw new InvalidOperationException(
                $"{named} is recorded as a fragment rather than a complete statement, so running it " +
                "would create something other than the source's trigger.");
        }

        RequireDialect(dialect, native.Dialect, named);

        return $"{native.Text.TrimEnd(';')};";
    }

    /// <summary>
    /// Appends the <c>PARTITION BY</c> clause of a table the source declared partitioned, which a
    /// partition can be as well: PostgreSQL lets a partition be partitioned again.
    /// </summary>
    private static void AppendPartitionBy(StringBuilder sql, TableDefinition table)
    {
        if (table.TableKind == ETableKind.Partitioned && !string.IsNullOrEmpty(table.PartitionStrategy))
        {
            sql.Append($" PARTITION BY {table.PartitionStrategy}");
        }
    }

    private static string JoinIdentifiers(IReadOnlyList<string> identifiers) =>
        string.Join(", ", identifiers.Select(PgIdentifierQuoter.QuoteIdentifier));

    /// <summary>
    /// Removes the trailing <c>NOT VALID</c> a recorded constraint expression carries for a
    /// constraint the source never validated, which only <c>ALTER TABLE ADD CONSTRAINT</c> accepts.
    /// </summary>
    private static string StripNotValid(string expression)
    {
        const string suffix = " NOT VALID";

        return expression.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? expression[..^suffix.Length].TrimEnd()
            : expression;
    }

    /// <summary>
    /// Runs the captured routine text, which is already a complete <c>CREATE</c> statement.
    /// </summary>
    private static IReadOnlyList<string> CreateRoutineStatements(
        string dialect,
        IReadOnlyList<ISchemaObject> objects)
    {
        var statements = new List<string>(objects.Count);

        foreach (var obj in objects)
        {
            if (obj is not RoutineDefinition routine)
            {
                throw WrongObject(SchemaStage.Routines, nameof(RoutineDefinition), obj);
            }

            // The statement text carries the routine's own schema, so the name is not needed to
            // build it — but a routine whose schema the import cannot name belongs to a catalog that
            // does not describe its own contents, and that is worth stopping for.
            RequireSchema(routine.SchemaRef, $"the routine \"{routine.Name}\"");

            var native = routine.NativeDefinition;
            if (native is null || string.IsNullOrWhiteSpace(native.Text))
            {
                throw new InvalidOperationException(
                    $"The routine \"{routine.Name}\" carries no captured body, so there is nothing to " +
                    "execute. Reconstructing one from the recorded signature would invent an " +
                    "implementation the source never had.");
            }

            RequireDialect(dialect, native.Dialect, $"The routine \"{routine.Name}\"");
            statements.Add($"{native.Text.TrimEnd(';')};");
        }

        return statements;
    }

    /// <summary>
    /// Creates views and materialized views from the query body the catalog recorded.
    /// </summary>
    private static IReadOnlyList<string> CreateViewStatements(
        string dialect,
        IReadOnlyList<ISchemaObject> objects)
    {
        var views = new List<Models.ViewDefinition>();
        var materializedViews = new List<Models.MaterializedViewDefinition>();

        foreach (var obj in objects)
        {
            if (obj is not ViewDefinition view)
            {
                throw WrongObject(SchemaStage.Views, nameof(ViewDefinition), obj);
            }

            var schemaName = RequireSchema(view.SchemaRef, $"the view \"{view.Name}\"");
            var body = DefinitionText(dialect, view);

            if (view.IsMaterialized)
            {
                // The data is not the definition: a materialized view is created empty, exactly as
                // the copy pipeline creates it, and filled by a refresh the import does not own.
                materializedViews.Add(new Models.MaterializedViewDefinition(
                    schemaName, view.Name, body, Tablespace: null, Columns: [], Comment: null));
            }
            else
            {
                views.Add(new Models.ViewDefinition(schemaName, view.Name, body, Comment: null));
            }
        }

        return
        [
            .. Ddl.GenerateCreateViews(views),
            .. Ddl.GenerateCreateMaterializedViews(materializedViews),
        ];
    }

    /// <summary>
    /// Reads the query body a view object carries, which the format stores as a fragment.
    /// </summary>
    private static string DefinitionText(string dialect, ViewDefinition view)
    {
        var named = $"The view \"{view.Name}\"";

        // The reader records a view body twice — as the format's expression and as native text — and
        // both carry the same statement, so the only questions here are whether the text is there and
        // which dialect it is written in.
        var text = view.Definition?.Text ?? view.NativeDefinition?.Text;
        var recordedDialect = view.Definition?.Dialect ?? view.NativeDefinition?.Dialect;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"{named} carries no definition text, so it cannot be created.");
        }

        RequireDialect(dialect, recordedDialect ?? string.Empty, named);
        return text;
    }

    /// <summary>
    /// Names the schema an object belongs to, or stops the import rather than guessing one.
    /// </summary>
    private static string RequireSchema(ObjectRef schemaRef, string objectName)
    {
        var schema = schemaRef.ObjectName();
        if (string.IsNullOrWhiteSpace(schema))
        {
            throw new InvalidOperationException(
                $"The catalog being imported does not name the schema {objectName} lives in, so the " +
                "object cannot be created in a schema chosen by guess.");
        }

        return schema;
    }

    /// <summary>
    /// Names the table an object points at by reference, or stops the import rather than guessing one.
    /// </summary>
    /// <remarks>
    /// <paramref name="referringObject"/> is the pointing object's own name, quoted, as the catalog
    /// spells it; it appears in the message so the refusal says whose link is unanswered.
    /// </remarks>
    private static string RequireTableName(
        ObjectRef tableRef,
        string referringObject)
    {
        var schema = tableRef.SchemaName();
        var name = tableRef.ObjectName();
        if (!string.IsNullOrWhiteSpace(schema) && !string.IsNullOrWhiteSpace(name))
        {
            return PgIdentifierQuoter.QuoteSchemaQualified(schema, name);
        }

        throw new InvalidOperationException(
            $"The catalog being imported holds no name for the table {referringObject} points at, so " +
            "the link cannot be created against a table chosen by guess.");
    }

    /// <summary>
    /// Checks that recorded SQL text was written in the dialect this adapter speaks.
    /// </summary>
    /// <remarks>
    /// A body from another dialect fails on the server with a syntax error that names no dialect, so
    /// checking here turns a confusing rejection into the actual fact: this catalog is not for this
    /// engine. Text translation is the format's own stage and has not run.
    /// <para>
    /// <paramref name="expectedDialect"/> is passed rather than read from the adapter, because the same
    /// builders answer both the restore, which applies text against a live target, and the script
    /// renderer, which writes that same text into a file — and only the former holds an adapter.
    /// </para>
    /// </remarks>
    private static void RequireDialect(
        string expectedDialect,
        string dialect,
        string objectName)
    {
        if (!string.Equals(dialect, expectedDialect, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{objectName} is written in the \"{dialect}\" dialect, which this " +
                $"\"{expectedDialect}\" adapter cannot execute.");
        }
    }

    private static ArgumentException WrongObject(
        SchemaStage stage,
        string expected,
        ISchemaObject obj) =>
        new(
            $"The {stage} stage expects {expected} objects, not {obj.GetType().Name}.",
            "objects");

    /// <summary>
    /// Opens the connection the stages run on. DDL needs no snapshot, so the connection stays in
    /// autocommit and each stage opens its own transaction.
    /// </summary>
    private async Task<NpgsqlConnection> EnsureConnectionAsync(CancellationToken ct)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        var builder = PgConnectionStringBuilder.ParseConnectionString(_target);
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        _connection = connection;

        return connection;
    }

    /// <summary>
    /// The columns a restore writes, and where each one's value sits in a stored row.
    /// </summary>
    /// <param name="Column">The column name the catalog recorded.</param>
    /// <param name="Type">The engine's own type text, used to read the stored token back.</param>
    /// <param name="RowIndex">The position of the value in the row, counted over every column the
    /// catalog recorded, including the ones a restore cannot write.</param>
    internal readonly record struct RowTarget(string Column, string Type, int RowIndex);

    /// <summary>
    /// Decides which columns a restore writes and which slot of a stored row answers each one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A chunk stores a row as positional text: the value of the first recorded column first, and so
    /// on. Nothing in the chunk says which column that was, and <c>DataValue</c>'s own type field comes
    /// from the table manifest rather than from the row, so the position has to be read off the
    /// catalog's column records in the order the export selected them — by ordinal.
    /// </para>
    /// <para>
    /// A generated column keeps its slot in the row and is left out of the statement, because
    /// PostgreSQL refuses an <c>INSERT</c> that names it: the token the catalog holds for it is the
    /// source's computed result, and the target computes its own.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<RowTarget> BuildRowTargets(IReadOnlyList<ColumnDefinition> columns)
    {
        var ordered = columns.OrderBy(c => c.Ordinal).ToList();
        var targets = new List<RowTarget>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            var column = ordered[i];

            if (column.Generated is not null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(column.NativeType.Name))
            {
                throw new InvalidOperationException(
                    $"The catalog records no native type for column \"{column.Name}\", so the text " +
                    "stored for it cannot be read back as that type.");
            }

            targets.Add(new RowTarget(column.Name, column.NativeType.Name, i));
        }

        return targets;
    }

    /// <summary>
    /// Builds the statement that writes one batch.
    /// </summary>
    /// <remarks>
    /// The <c>parameters</c> list is where a stored binary value's parameter is added, so the caller can attach
    /// it to the command. Leaving it out is only for building a batch's text, which means a batch holding no
    /// payload — unless <paramref name="inlinePayloads"/> is set, which is the script renderer's case: a file
    /// has no parameters, so the bytes are written into the statement instead (D5).
    /// </remarks>
    internal static string BuildInsertStatement(
        string tableName,
        string columnList,
        IReadOnlyList<RowTarget> targets,
        IReadOnlyList<DataValue[]> batch,
        List<NpgsqlParameter>? parameters = null,
        bool inlinePayloads = false)
    {
        // R7: the statement is built directly into a StringBuilder, appending each row's values
        // in place — no intermediate string per value, no Join, no ToString chain.
        var sql = new StringBuilder();
        sql.Append("INSERT INTO ").Append(tableName).Append(" (").Append(columnList).Append(") VALUES ");

        for (var r = 0; r < batch.Count; r++)
        {
            if (r > 0)
            {
                sql.Append(", ");
            }

            sql.Append('(');
            AppendRowValues(sql, targets, batch[r], parameters, inlinePayloads);
            sql.Append(')');
        }

        sql.Append(';');
        return sql.ToString();
    }

    /// <summary>
    /// Appends one stored row's values directly into the statement's <see cref="StringBuilder"/>,
    /// avoiding the intermediate string array and <c>string.Join</c> the original design used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A token is the text the engine's own output function produced, so it is handed back through the
    /// same type's input function by casting the literal — <c>'12.50'::numeric(10,2)</c>. A literal is
    /// used rather than a bound parameter because the types here are the source's own: an enum, a
    /// domain, a composite. PostgreSQL reads a literal in such a type directly, while a parameter
    /// arrives already typed as text and no cast from text to a user-defined type exists. It is the
    /// mechanism the copy pipeline's <c>INSERT</c> path uses, for the same reason.
    /// </para>
    /// <para>
    /// A value stored out-of-band is the exception to that rule, and the reason the rule was never meant for
    /// it: its bytes were never rendered as text by anything. Handing them back as a <c>bytea</c> parameter over
    /// a stream keeps them out of the statement text entirely — no hex inside SQL, no whole value as one string
    /// on either side (design §5, §6.9) — and a <c>bytea</c> is a built-in type, so a typed parameter reads it
    /// correctly, which is precisely the case the literal rule exists to cover for user-defined types.
    /// </para>
    /// <para>
    /// A script has no parameters, so <paramref name="inlinePayloads"/> reverses that one rule and writes the
    /// bytes into the statement as base64 (D5). Base64 is chosen over hex because it is 4/3 of the payload
    /// against 2×, and because its alphabet cannot contain the quote that would end the literal. One value is
    /// one very long line: a string literal cannot be wrapped, since a newline inside it would become part of
    /// the value.
    /// </para>
    /// <para>
    /// <paramref name="typedNulls"/> is for the one list that is typed before its target's columns are known:
    /// a <c>VALUES</c> block a statement reads back by name. There a bare <c>NULL</c> would be read as text
    /// once every value of its column is null, so the cast names the type the value is missing from.
    /// </para>
    /// </remarks>
    internal static void AppendRowValues(
        StringBuilder sql,
        IReadOnlyList<RowTarget> targets,
        DataValue[] row,
        List<NpgsqlParameter>? parameters = null,
        bool inlinePayloads = false,
        bool typedNulls = false)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            var target = targets[i];

            // A row shorter than the columns recorded for the table means the catalog's data and its
            // schema disagree about the table's shape, so no value may be placed by guesswork.
            if (target.RowIndex >= row.Length)
            {
                throw new InvalidOperationException(
                    $"A stored row holds {row.Length} value(s), fewer than the {targets.Count} " +
                    $"column(s) the catalog records for this table, so the value of column " +
                    $"\"{target.Column}\" cannot be placed.");
            }

            var value = row[target.RowIndex];
            if (value.Binary is { } payload)
            {
                if (inlinePayloads)
                {
                    sql.Append("decode('");
                    sql.Append(EncodePayloadAsBase64(payload));
                    sql.Append("', 'base64')::");
                    sql.Append(target.Type);
                }
                else if (parameters is null)
                {
                    throw new InvalidOperationException(
                        $"The value of column \"{target.Column}\" is stored as bytes outside the row file, " +
                        "so it can only be written as a parameter, and no parameter list was given.");
                }
                else
                {
                    // One parameter per value, so the placeholder names count the payloads the batch holds.
                    var name = $"blob{parameters.Count}";
                    parameters.Add(
                        new NpgsqlParameter(name, NpgsqlDbType.Bytea)
                        {
                            Value = payload.OpenRead(),
                        });
                    sql.Append('@').Append(name);
                }
            }
            else if (value.IsNull || value.Value is null)
            {
                sql.Append("NULL");

                if (typedNulls)
                {
                    sql.Append("::").Append(target.Type);
                }
            }
            else
            {
                sql.Append('\'');
                sql.Append(value.Value.Replace("'", "''"));
                sql.Append("'::");
                sql.Append(target.Type);
            }
        }
    }

    /// <summary>
    /// Reads a stored payload and encodes it as the base64 text a script writes into a statement.
    /// </summary>
    /// <remarks>
    /// The renderer reads the stream the value carries and never opens a container of its own, which is the
    /// same contract the parameter path relies on — so an unreadable payload fails here, by the table's own
    /// name, rather than being written as a null.
    /// </remarks>
    internal static string EncodePayloadAsBase64(IBinaryPayload payload)
    {
        using var stream = payload.OpenRead();
        using var buffer = new MemoryStream(
            payload.Length is > 0 and <= int.MaxValue ? (int)payload.Length : 0);
        stream.CopyTo(buffer);

        return Convert.ToBase64String(buffer.ToArray());
    }

    /// <summary>
    /// Writes one batch in its own transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both settings are local to the batch because the statement is built per batch. The time zone
    /// matters because a <c>timestamptz</c> was exported as the UTC rendering of the instant it held, so
    /// reading that text back as an instant needs a session that names UTC — a target configured for
    /// another zone would otherwise move every timestamp by the difference. Quoting the stored text as a
    /// literal needs <c>standard_conforming_strings</c> on, the setting that makes a backslash an
    /// ordinary character; with it off, a value ending in a backslash would eat the closing quote.
    /// </para>
    /// <para>
    /// A stored binary value is not in that text at all: it is one parameter over its own stream, which the
    /// server reads while the statement runs. Npgsql closes nothing it was handed, so the batch releases the
    /// streams it opened — which is also what keeps the table's payloads bounded to one batch instead of
    /// accumulating for the whole import.
    /// </para>
    /// </remarks>
    private static async Task<long> InsertBatchAsync(
        NpgsqlConnection connection,
        string tableName,
        string columnList,
        IReadOnlyList<RowTarget> targets,
        List<DataValue[]> batch,
        CancellationToken ct)
    {
        var parameters = new List<NpgsqlParameter>();
        var sql = BuildInsertStatement(tableName, columnList, targets, batch, parameters);

        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using (
                var utc = new NpgsqlCommand("SET LOCAL TIME ZONE 'UTC'", connection, transaction))
            {
                await utc.ExecuteNonQueryAsync(ct);
            }

            await using (
                var literals = new NpgsqlCommand(
                    "SET LOCAL standard_conforming_strings = 'on'",
                    connection,
                    transaction))
            {
                await literals.ExecuteNonQueryAsync(ct);
            }

            await using (
                var insert = new NpgsqlCommand(sql, connection, transaction)
                {
                    CommandTimeout = 300,
                })
            {
                foreach (var parameter in parameters)
                {
                    insert.Parameters.Add(parameter);
                }

                await insert.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            foreach (var parameter in parameters)
            {
                if (parameter.Value is Stream stream)
                {
                    await stream.DisposeAsync();
                }
            }
        }

        return batch.Count;
    }

    /// <summary>
    /// Counts the target's objects by the kind the interchange model names.
    /// </summary>
    /// <remarks>
    /// A kind the live model has no collection for is left out of the dictionary rather than answered
    /// with zero: an absent key is reported by the caller as a check that did not run, while a zero
    /// would be reported as a target holding nothing — a wrong fact about the database, stated as an
    /// error.
    /// </remarks>
    internal static IReadOnlyDictionary<EObjectKind, int> CountTargetObjects(
        Models.DatabaseModel model,
        IReadOnlyList<string>? catalogSchemaNames = null) =>
        new Dictionary<EObjectKind, int>
        {
            // A system schema is counted only when the catalog names it: the target holds its own
            // copy whatever a restore does, so a release that captured system schemas is answered
            // by the presence it recorded rather than reported as a target holding too many. A
            // system schema the target lacks stays uncounted — the restore cannot create it either.
            [EObjectKind.Schema] = model.Schemas.Count(
                s => !s.IsSystem
                     || catalogSchemaNames?.Contains(s.Name, StringComparer.OrdinalIgnoreCase) is true),
            [EObjectKind.Table] = model.Tables.Count,
            [EObjectKind.View] = model.Views.Count,
            [EObjectKind.MaterializedView] = model.MaterializedViews.Count,
            [EObjectKind.Sequence] = model.Sequences.Count,
            [EObjectKind.Enum] = model.Enums.Count,
            [EObjectKind.Domain] = model.Domains.Count,
            [EObjectKind.CompositeType] = model.CompositeTypes.Count,

            // The reader reports a procedure beside a function, and the interchange model separates
            // them by kind, so the two are counted apart.
            [EObjectKind.Function] = model.Functions.Count(f => !f.IsProcedure),
            [EObjectKind.Procedure] = model.Functions.Count(f => f.IsProcedure),
        };

    /// <summary>
    /// Counts the rows a table holds.
    /// </summary>
    /// <remarks>
    /// <c>FROM ONLY</c> is deliberately absent: the export read a partitioned table through the plain
    /// name, which answers the rows held in its partitions, and a count taken with <c>ONLY</c> would
    /// answer none of them against that.
    /// </remarks>
    private static async Task<long> ReadTableRowCountAsync(
        NpgsqlConnection connection,
        TableRef table,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {QuoteTableName(table)}",
            connection)
        {
            CommandTimeout = 300,
        };

        var count = await command.ExecuteScalarAsync(ct);

        return count is long value ? value : Convert.ToInt64(count);
    }

    private static string QuoteTableName(TableRef table) =>
        string.IsNullOrEmpty(table.Schema)
            ? PgIdentifierQuoter.QuoteIdentifier(table.Name)
            : PgIdentifierQuoter.QuoteSchemaQualified(table.Schema, table.Name);

    private static string QualifiedName(TableRef table) =>
        string.IsNullOrEmpty(table.Schema) ? table.Name : $"{table.Schema}.{table.Name}";

    private static string Pluralize(string noun, int count) => count == 1 ? noun : $"{noun}s";
}
