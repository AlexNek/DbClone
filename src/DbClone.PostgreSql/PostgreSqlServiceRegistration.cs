using DbClone.Application.Interfaces;
using DbClone.Application.Copy;
using DbClone.Application.Platforms;
using DbClone.Application.Services;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.DependencyAnalysis;
using DbClone.PostgreSql.Execution;
using DbClone.PostgreSql.Formats;
using DbClone.PostgreSql.Pipeline;
using DbClone.PostgreSql.Providers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbClone.PostgreSql;

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
        services.AddSingleton<IDdlGenerator>(sp => sp.GetRequiredService<PgDdlGenerator>());
        services.AddSingleton<IPgExecutorFactory, PgExecutorFactory>();

        // Pipeline stages (registered as ICopyStage implementations)
        services.AddTransient<ICopyStage, ConnectStage>();
        services.AddTransient<ICopyStage, DetectCapabilitiesStage>();
        services.AddTransient<ICopyStage, ReadMetadataStage>();
        services.AddTransient<ICopyStage, ApplyTableFilterStage>();
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

        // Provider services for UI-layer consumption
        services.AddTransient<ITableInfoProvider, PgTableInfoProvider>();
        services.AddTransient<IDatabaseMaintenanceProvider, PgDatabaseMaintenanceProvider>();
        services.AddTransient<ITableComparerProvider, PgTableComparerProvider>();
        services.AddSingleton<IConnectionStringService, PgConnectionStringService>();

        // Connection string format plugins (import/export)
        services.AddSingleton<IConnectionFormat, PostgreSqlSupabaseEnvFormat>();
        services.AddSingleton<IConnectionFormat, PostgreSqlSupabaseFormat>();
        services.AddSingleton<IConnectionFormat, PostgreSqlUriFormat>();
        services.AddSingleton<IConnectionFormat, PostgreSqlJdbcFormat>();
        services.AddSingleton<IConnectionFormat, PostgreSqlNpgsqlFormat>();
        services.AddSingleton<IConnectionFormat, PostgreSqlLibpqFormat>();
        services.AddSingleton<IConnectionFormat, PostgreSqlEnvVarFormat>();
        services.AddSingleton<IConnectionFormat, PostgreSqlSqlAlchemyFormat>();
        services.AddSingleton<IConnectionFormat, PostgreSqlPrismaFormat>();
        services.AddSingleton<IConnectionFormat, PostgreSqlNodeFormat>();

        // Import/Export orchestration services
        services.AddSingleton<IConnectionImportService, ConnectionImportService>();
        services.AddSingleton<IConnectionExportService, ConnectionExportService>();

        return services;
    }
}
