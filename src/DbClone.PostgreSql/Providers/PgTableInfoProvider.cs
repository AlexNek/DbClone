using DbClone.Application.DTOs;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.Application.Platforms;
using DbClone.PostgreSql.Execution;
using DbClone.PostgreSql.Metadata;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Providers;

public sealed class PgTableInfoProvider : ITableInfoProvider
{
    private readonly ILogger<PgTableInfoProvider> _logger;

    private readonly ILoggerFactory _loggerFactory;

    private readonly PlatformSchemaResolver _platformResolver;

    public PgTableInfoProvider(
        ILogger<PgTableInfoProvider> logger,
        ILoggerFactory loggerFactory,
        PlatformSchemaResolver platformResolver)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _platformResolver = platformResolver;
    }

    public async Task<List<(string Schema, string Name)>> GetTablesAsync(
        ConnectionInfo connection,
        CancellationToken ct)
    {
        try
        {
            var builder = PgConnectionStringBuilder.BuildConnectionString(connection);
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(
                $@"SELECT n.nspname, c.relname
                  FROM pg_class c
                  JOIN pg_namespace n ON n.oid = c.relnamespace
                  WHERE c.relkind IN ({PgRelKind.TableOrPartition})
                    AND n.nspname NOT IN ({PgSystemSchemas.SqlList})
                    AND n.nspname NOT LIKE 'pg_temp_%'
                  ORDER BY n.nspname, c.relname",
                conn);

            var tables = new List<(string Schema, string Name)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }

            return tables;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tables from {Db}", connection.DatabaseName);
            throw;
        }
    }

    public async Task<DatabaseModel> ReadDatabaseModelAsync(
        ConnectionInfo connection,
        bool excludePlatformSchemas = false,
        CancellationToken ct = default)
    {
        var builder = PgConnectionStringBuilder.BuildConnectionString(connection);
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);

        var resolution = _platformResolver.Resolve(
            "postgresql", connection.Host, conn.ServerVersion);

        var executor = new PgSqlExecutor(conn, _loggerFactory.CreateLogger<PgSqlExecutor>());
        var reader = new PgMetadataReader(
            executor,
            _loggerFactory.CreateLogger<PgMetadataReader>());
        return await reader.ReadDatabaseModelAsync(excludePlatformSchemas, resolution, ct);
    }
}
