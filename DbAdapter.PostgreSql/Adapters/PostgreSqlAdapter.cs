using System.IO;

using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Compare;
using DbAdapter.PostgreSql.Ddl;
using DbAdapter.PostgreSql.DependencyAnalysis;
using DbAdapter.PostgreSql.Execution;
using DbAdapter.PostgreSql.Formats;
using DbAdapter.PostgreSql.Pipeline;
using DbAdapter.PostgreSql.Platforms;
using DbAdapter.PostgreSql.Providers;

using Microsoft.Extensions.Logging;

namespace DbAdapter.PostgreSql.Adapters;

/// <summary>
/// The PostgreSQL adapter: the seven roles of one engine, composed and handed out.
/// The adapter itself does no work — every role creates the class that has always
/// answered that question, scoped to the target the caller supplies.
/// <para>
/// The adapter is self-composing: the plugin catalog constructs it with a logger
/// factory and the folder it was loaded from, and it builds its own executor,
/// stage repository, platform resolver and definition loader over that folder —
/// no container, so a block installable into <c>plugins/&lt;dialect&gt;</c> needs
/// nothing the host must know about (refactors/09 P3/P10).
/// </para>
/// </summary>
public sealed class PostgreSqlAdapter : IDatabaseAdapter
{
    private readonly ILoggerFactory _loggerFactory;

    private readonly PlatformSchemaResolver _platformResolver;

    private readonly ICopyEngine _copyEngine;

    /// <inheritdoc />
    public AdapterInfo Info { get; }

    /// <inheritdoc />
    public string ProviderName => "PostgreSQL";

    /// <inheritdoc />
    public IConnectionStringSyntax Syntax { get; }

    /// <inheritdoc />
    public IPlatformResolver Platform { get; }

    /// <summary>
    /// Composition entry point used by the plugin catalog. Builds the whole engine object
    /// graph from <paramref name="pluginDirectory"/>, so the host never names a block type.
    /// </summary>
    public PostgreSqlAdapter(ILoggerFactory loggerFactory, string pluginDirectory)
        : this(ComposeGraph(loggerFactory, pluginDirectory), loggerFactory)
    {
    }

    /// <summary>Initializes a new instance from a pre-composed engine graph.</summary>
    private PostgreSqlAdapter(EngineGraph graph, ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _platformResolver = graph.PlatformResolver;
        _copyEngine = graph.CopyEngine;

        Info = new AdapterInfo(
            Id: "org.dbclone.postgresql",
            Dialect: "postgresql",
            EngineVersionRange: "*",
            AdapterVersion: typeof(PostgreSqlAdapter).Assembly.GetName().Version?.ToString()
                            ?? "0.0.0");
        Syntax = new ConnectionStringSyntaxAdapter(graph.ConnectionStringService, graph.Formats);
        Platform = new PlatformResolverAdapter(graph.PlatformResolver);
    }

    /// <inheritdoc />
    public IMetadataSource CreateMetadataSource(DatabaseTarget target) =>
        new PgTableInfoProvider(
            target,
            _loggerFactory.CreateLogger<PgTableInfoProvider>(),
            _loggerFactory,
            _platformResolver);

    /// <inheritdoc />
    public IDatabaseAdmin CreateAdmin(DatabaseTarget target) =>
        new PgDatabaseAdmin(target, _loggerFactory.CreateLogger<PgDatabaseAdmin>());

    /// <inheritdoc />
    public IDatabaseCleaner CreateCleaner(DatabaseTarget target) =>
        new PgDatabaseCleaner(target, _loggerFactory.CreateLogger<PgDatabaseCleaner>());

    /// <inheritdoc />
    public ITableComparer CreateComparer() =>
        new PgTableComparerProvider(_loggerFactory.CreateLogger<PgTableComparerProvider>());

    /// <inheritdoc />
    public IReadOnlyList<IModelComparer> CreateModelComparers() =>
        new IModelComparer[] { new TableDdlComparer() };

    /// <inheritdoc />
    public ICopySession CreateCopySession() => new CopySessionAdapter(_copyEngine);

    /// <summary>The internal roles a composed adapter hands to its boundary.</summary>
    private sealed record EngineGraph(
        ICopyEngine CopyEngine,
        PgConnectionStringService ConnectionStringService,
        IReadOnlyList<IConnectionTextFormat> Formats,
        PlatformSchemaResolver PlatformResolver);

    /// <summary>
    /// Builds the PostgreSQL object graph by hand — the equivalent of the retired
    /// <c>AddPostgreSqlProvider()</c> registrations, but container-free so a loaded plugin
    /// composes itself. The platform files are read from within <paramref name="pluginDirectory"/>
    /// so the block resolves its own data, never the application's folder (refactors/09 P3/P5).
    /// </summary>
    private static EngineGraph ComposeGraph(ILoggerFactory loggerFactory, string pluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var platformsDir = Path.Combine(pluginDirectory, "platforms", "postgresql");
        var loader = new PlatformDefinitionLoader(
            platformsDir,
            loggerFactory.CreateLogger<PlatformDefinitionLoader>());
        var platformResolver = new PlatformSchemaResolver(
            loader,
            loggerFactory.CreateLogger<PlatformSchemaResolver>());

        var ddl = new PgDdlGenerator();
        var analyzer = new PgDependencyAnalyzer(loggerFactory.CreateLogger<PgDependencyAnalyzer>());
        IPgExecutorFactory executorFactory = new PgExecutorFactory(loggerFactory);
        IPgConnectionProvider connectionProvider = new PgConnectionProvider();

        // The runner sorts by each stage's Order, so listing order is not significant; all 20
        // stages the retired registration added are supplied here, unchanged.
        IReadOnlyList<ICopyStage> stages =
        [
            new ConnectStage(loggerFactory.CreateLogger<ConnectStage>(), platformResolver),
            new DetectCapabilitiesStage(loggerFactory),
            new ReadMetadataStage(loggerFactory),
            new AnalyzeDependenciesStage(analyzer),
            new CreateSchemasStage(ddl, loggerFactory),
            new CreateExtensionsStage(loggerFactory),
            new CreateSequencesStage(ddl, loggerFactory),
            new CreateTypesStage(ddl, loggerFactory),
            new CreateFunctionsStage(ddl, loggerFactory),
            new CreateTablesStage(ddl, executorFactory, loggerFactory),
            new ReconcileColumnsStage(executorFactory, loggerFactory),
            new CopyDataStage(executorFactory, loggerFactory),
            new CreateIndexesStage(ddl, executorFactory, connectionProvider, loggerFactory),
            new CreateConstraintsStage(ddl, loggerFactory),
            new SyncSequencesStage(ddl, executorFactory, loggerFactory),
            new RetryFunctionsStage(ddl, loggerFactory),
            new CreateViewsStage(ddl, loggerFactory),
            new CreateTriggersStage(ddl, loggerFactory),
            new ValidateStage(executorFactory, loggerFactory),
            new ReCopyMismatchedStage(loggerFactory),
        ];

        ICopyPipeline pipeline = new CopyPipeline(stages, loggerFactory.CreateLogger<CopyPipeline>());
        ICopyEngine copyEngine = new PgCopyEngine(pipeline, loggerFactory.CreateLogger<PgCopyEngine>());

        var connectionStringService = new PgConnectionStringService();
        IReadOnlyList<IConnectionTextFormat> formats =
        [
            new PostgreSqlSupabaseEnvFormat(),
            new PostgreSqlSupabaseFormat(),
            new PostgreSqlUriFormat(),
            new PostgreSqlJdbcFormat(),
            new PostgreSqlNpgsqlFormat(),
            new PostgreSqlLibpqFormat(),
            new PostgreSqlEnvVarFormat(),
            new PostgreSqlSqlAlchemyFormat(),
            new PostgreSqlPrismaFormat(),
            new PostgreSqlNodeFormat(),
        ];

        return new EngineGraph(copyEngine, connectionStringService, formats, platformResolver);
    }
}
