using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DbAdapter.PostgreSql.Ddl;

using Microsoft.Extensions.Logging;

using Npgsql;

using Models = CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Interchange;

/// <summary>
/// PostgreSQL implementation of <see cref="IDatabaseWriter"/>.
/// <para>
/// The writer brings no query of its own to the import: the target check reads through
/// <see cref="IMetadataSource"/>, the same role copy and compare use, and the statements that
/// create an object come from <see cref="PgDdlGenerator"/>, the same generator the copy pipeline
/// runs. What belongs to the import alone is the order — the stages, and the transaction that
/// makes each stage the commit unit.
/// </para>
/// <para>
/// A stage whose objects this writer cannot build is refused, and so are rows, and so is
/// post-import validation. Applying nothing and reporting success is the outcome the format's rules
/// name explicitly ("nothing is silently dropped"), and a stub that swallows a row batch would be
/// indistinguishable from a restore that worked.
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
        ObjectNameMap names,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(names);

        if (objects.Count == 0)
        {
            // An empty stage says the catalog holds nothing of that kind. That is a fact about the
            // source, not a limitation of the adapter, so it is not a failure.
            return;
        }

        var statements = BuildStatements(stage, objects, names);

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
    public Task WriteTableRowsAsync(
        TableRef table,
        IAsyncEnumerable<DataValue[]> rows,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(rows);

        throw new NotSupportedException(
            $"Writing rows is not implemented yet, so the rows of {QualifiedName(table)} cannot " +
            "be applied. Reading the batch and discarding it would let the import report them " +
            "applied.");
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
    public Task<ImportValidationReport> ValidateAsync(
        TargetExpectation expectation,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(expectation);

        // A report of "passed" from a writer that counted nothing would be the loudest claim this
        // class could make, so validation refuses until it can query the target.
        throw new NotSupportedException(
            "Post-import validation is not implemented yet, so the target cannot be checked " +
            "against the catalog.");
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
    /// <see cref="InterchangeId"/> alone. <paramref name="names"/> carries the answers, resolved by
    /// whoever read the catalog, since an adapter never opens one. An object whose parent the map
    /// does not name stops the import: creating a sequence in the default schema because its schema
    /// id went unanswered would relocate the object and still report the stage as applied.
    /// </para>
    /// <para>
    /// Schemas, sequences and views are converted into the live model records
    /// <see cref="PgDdlGenerator"/> works over, so a restore builds an object with the statements the
    /// copy pipeline already uses. Routines need no generator: <c>pg_get_functiondef</c> handed the
    /// catalog a complete <c>CREATE</c> statement, and rebuilding one from the parsed signature would
    /// be a rewrite of text that is already exact.
    /// </para>
    /// </remarks>
    internal IReadOnlyList<string> BuildStatements(
        SchemaStage stage,
        IReadOnlyList<ISchemaObject> objects,
        ObjectNameMap names) =>
        stage switch
        {
            SchemaStage.Schemas => CreateSchemaStatements(objects),
            SchemaStage.Sequences => CreateSequenceStatements(objects, names),
            SchemaStage.Routines => CreateRoutineStatements(objects, names),
            SchemaStage.Views => CreateViewStatements(objects, names),
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

            // Ownership is not part of this statement. The import assigns owners in its own security
            // stage, once the roles exist, so the schema is created owned by the connecting role.
            schemas.Add(new Models.SchemaDefinition(schema.Name, Owner: string.Empty));
        }

        return Ddl.GenerateCreateSchemas(schemas);
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
        IReadOnlyList<ISchemaObject> objects,
        ObjectNameMap names)
    {
        var sequences = new List<Models.SequenceDefinition>(objects.Count);

        foreach (var obj in objects)
        {
            if (obj is not SequenceDefinition sequence)
            {
                throw WrongObject(SchemaStage.Sequences, nameof(SequenceDefinition), obj);
            }

            sequences.Add(new Models.SequenceDefinition(
                RequireSchema(names, sequence.SchemaId, $"the sequence \"{sequence.Name}\""),
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
    /// Runs the captured routine text, which is already a complete <c>CREATE</c> statement.
    /// </summary>
    private IReadOnlyList<string> CreateRoutineStatements(
        IReadOnlyList<ISchemaObject> objects,
        ObjectNameMap names)
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
            RequireSchema(names, routine.SchemaId, $"the routine \"{routine.Name}\"");

            var native = routine.NativeDefinition;
            if (native is null || string.IsNullOrWhiteSpace(native.Text))
            {
                throw new InvalidOperationException(
                    $"The routine \"{routine.Name}\" carries no captured body, so there is nothing to " +
                    "execute. Reconstructing one from the recorded signature would invent an " +
                    "implementation the source never had.");
            }

            RequireDialect(native.Dialect, $"The routine \"{routine.Name}\"");
            statements.Add($"{native.Text.TrimEnd(';')};");
        }

        return statements;
    }

    /// <summary>
    /// Creates views and materialized views from the query body the catalog recorded.
    /// </summary>
    private IReadOnlyList<string> CreateViewStatements(
        IReadOnlyList<ISchemaObject> objects,
        ObjectNameMap names)
    {
        var views = new List<Models.ViewDefinition>();
        var materializedViews = new List<Models.MaterializedViewDefinition>();

        foreach (var obj in objects)
        {
            if (obj is not ViewDefinition view)
            {
                throw WrongObject(SchemaStage.Views, nameof(ViewDefinition), obj);
            }

            var schemaName = RequireSchema(names, view.SchemaId, $"the view \"{view.Name}\"");
            var body = DefinitionText(view);

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
    private string DefinitionText(ViewDefinition view)
    {
        var named = $"The view \"{view.Name}\"";

        // The reader records a view body twice — as the format's expression and as native text — and
        // both carry the same statement, so the only questions here are whether the text is there and
        // which dialect it is written in.
        var text = view.Definition?.Text ?? view.NativeDefinition?.Text;
        var dialect = view.Definition?.Dialect ?? view.NativeDefinition?.Dialect;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"{named} carries no definition text, so it cannot be created.");
        }

        RequireDialect(dialect ?? string.Empty, named);
        return text;
    }

    /// <summary>
    /// Names the schema an object belongs to, or stops the import rather than guessing one.
    /// </summary>
    private static string RequireSchema(ObjectNameMap names, InterchangeId schemaId, string objectName)
    {
        var schema = names.SchemaName(schemaId);
        if (schema is null)
        {
            throw new InvalidOperationException(
                $"The catalog being imported does not name the schema {objectName} lives in, so the " +
                "object cannot be created in a schema chosen by guess.");
        }

        return schema;
    }

    /// <summary>
    /// Checks that recorded SQL text was written in the dialect this adapter speaks.
    /// </summary>
    /// <remarks>
    /// A body from another dialect fails on the server with a syntax error that names no dialect, so
    /// checking here turns a confusing rejection into the actual fact: this catalog is not for this
    /// engine. Text translation is the format's own stage and has not run.
    /// </remarks>
    private void RequireDialect(string dialect, string objectName)
    {
        if (!string.Equals(dialect, Adapter.Info.Dialect, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{objectName} is written in the \"{dialect}\" dialect, which this " +
                $"\"{Adapter.Info.Dialect}\" adapter cannot execute.");
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

    private static string QualifiedName(TableRef table) =>
        string.IsNullOrEmpty(table.Schema) ? table.Name : $"{table.Schema}.{table.Name}";

    private static string Pluralize(string noun, int count) => count == 1 ? noun : $"{noun}s";
}
