using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Platforms;
using DbAdapter.PostgreSql.Providers;
using DbClone.Application.Interfaces;

using Microsoft.Extensions.Logging;

namespace DbAdapter.PostgreSql.Adapters;

/// <summary>
/// The PostgreSQL adapter: the seven roles of one engine, composed and handed out.
/// The adapter itself does no work — every role creates the class that has always
/// answered that question, scoped to the target the caller supplies.
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

    /// <summary>Initializes a new instance.</summary>
    public PostgreSqlAdapter(
        ICopyEngine copyEngine,
        PgConnectionStringService connectionStringService,
        IEnumerable<IConnectionTextFormat> formats,
        PlatformSchemaResolver platformResolver,
        ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _platformResolver = platformResolver ?? throw new ArgumentNullException(nameof(platformResolver));
        _copyEngine = copyEngine ?? throw new ArgumentNullException(nameof(copyEngine));

        Info = new AdapterInfo(
            Dialect: "postgresql",
            EngineVersionRange: "*",
            AdapterVersion: typeof(PostgreSqlAdapter).Assembly.GetName().Version?.ToString()
                            ?? "0.0.0");
        Syntax = new ConnectionStringSyntaxAdapter(connectionStringService, formats);
        Platform = new PlatformResolverAdapter(platformResolver);
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
    public ICopySession CreateCopySession() => new CopySessionAdapter(_copyEngine);
}
