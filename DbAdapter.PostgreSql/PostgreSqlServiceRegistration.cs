using DbClone.Application.Interfaces;
using DbClone.Application.Copy;
using DbAdapter.PostgreSql.Platforms;
using DbClone.Application.Services;
using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Adapters;
using DbAdapter.PostgreSql.Ddl;
using DbAdapter.PostgreSql.DependencyAnalysis;
using DbAdapter.PostgreSql.Execution;
using DbAdapter.PostgreSql.Formats;
using DbAdapter.PostgreSql.Pipeline;
using DbAdapter.PostgreSql.Providers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbAdapter.PostgreSql;

/// <summary>
/// Extension methods for registering PostgreSQL provider services.
/// </summary>
public static class PostgreSqlServiceRegistration
{
    /// <summary>
    /// Registers PostgreSQL provider services with the dependency injection container.
    /// </summary>
    public static IServiceCollection AddPostgreSqlProvider(this IServiceCollection services)
    {
        // Platform schema definitions (provider-specific subfolder)
        services.AddSingleton(sp =>
        {
            var platformsDir = Path.Combine(AppContext.BaseDirectory, "platforms", "postgresql");
            return new PlatformDefinitionLoader(
                platformsDir,
                sp.GetRequiredService<ILogger<PlatformDefinitionLoader>>());
        });
        services.AddSingleton<PlatformSchemaResolver>();

        // Core components
        services.AddSingleton<PgDependencyAnalyzer>();
        services.AddSingleton<PgDdlGenerator>();
        services.AddSingleton<IPgExecutorFactory, PgExecutorFactory>();
        services.AddSingleton<IPgConnectionProvider, PgConnectionProvider>();

        // Pipeline stages (registered as ICopyStage implementations)
        services.AddTransient<ICopyStage, ConnectStage>();
        services.AddTransient<ICopyStage, DetectCapabilitiesStage>();
        services.AddTransient<ICopyStage, ReadMetadataStage>();
        services.AddTransient<ICopyStage, AnalyzeDependenciesStage>();
        services.AddTransient<ICopyStage, CreateSchemasStage>();
        services.AddTransient<ICopyStage, CreateExtensionsStage>();
        services.AddTransient<ICopyStage, CreateSequencesStage>();
        services.AddTransient<ICopyStage, CreateTypesStage>();
        services.AddTransient<ICopyStage, CreateFunctionsStage>();
        services.AddTransient<ICopyStage, CreateTablesStage>();
        services.AddTransient<ICopyStage, ReconcileColumnsStage>();
        services.AddTransient<ICopyStage, CopyDataStage>();
        services.AddTransient<ICopyStage, CreateIndexesStage>();
        services.AddTransient<ICopyStage, CreateConstraintsStage>();
        services.AddTransient<ICopyStage, SyncSequencesStage>();
        services.AddTransient<ICopyStage, RetryFunctionsStage>();
        services.AddTransient<ICopyStage, CreateViewsStage>();
        services.AddTransient<ICopyStage, CreateTriggersStage>();
        services.AddTransient<ICopyStage, ValidateStage>();
        services.AddTransient<ICopyStage, ReCopyMismatchedStage>();

        // Pipeline orchestrator
        services.AddTransient<ICopyPipeline, CopyPipeline>();

        // Copy engine
        services.AddTransient<ICopyEngine, PgCopyEngine>();

        // Connection string service
        services.AddSingleton<PgConnectionStringService>();

        // Connection string format plugins (import/export)
        services.AddSingleton<IConnectionTextFormat, PostgreSqlSupabaseEnvFormat>();
        services.AddSingleton<IConnectionTextFormat, PostgreSqlSupabaseFormat>();
        services.AddSingleton<IConnectionTextFormat, PostgreSqlUriFormat>();
        services.AddSingleton<IConnectionTextFormat, PostgreSqlJdbcFormat>();
        services.AddSingleton<IConnectionTextFormat, PostgreSqlNpgsqlFormat>();
        services.AddSingleton<IConnectionTextFormat, PostgreSqlLibpqFormat>();
        services.AddSingleton<IConnectionTextFormat, PostgreSqlEnvVarFormat>();
        services.AddSingleton<IConnectionTextFormat, PostgreSqlSqlAlchemyFormat>();
        services.AddSingleton<IConnectionTextFormat, PostgreSqlPrismaFormat>();
        services.AddSingleton<IConnectionTextFormat, PostgreSqlNodeFormat>();

        // Import/Export orchestration services
        services.AddSingleton<IConnectionImportService, ConnectionImportService>();
        services.AddSingleton<IConnectionExportService, ConnectionExportService>();

        // Adapter: the one boundary the application reaches the engine through.
        // The stateful roles are not registered — the adapter creates them per target
        // or per session, so they are never resolved from the container.
        services.AddSingleton<PostgreSqlAdapter>();
        services.AddSingleton<IDatabaseAdapter>(sp => sp.GetRequiredService<PostgreSqlAdapter>());
        services.AddSingleton<IConnectionStringSyntax>(sp => sp.GetRequiredService<PostgreSqlAdapter>().Syntax);
        services.AddSingleton<IPlatformResolver>(sp => sp.GetRequiredService<PostgreSqlAdapter>().Platform);

        return services;
    }
}
