using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Platforms;
using DbAdapter.PostgreSql.Execution;
using DbAdapter.PostgreSql.Metadata;

using Microsoft.Extensions.Logging;

using Npgsql;
using CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Providers;

/// <summary>
/// The metadata role for one target: what tables it holds, how large they are,
/// and what the full database model looks like.
/// </summary>
public sealed class PgTableInfoProvider : IMetadataSource
{
    private readonly DatabaseTarget _target;

    private readonly ILogger<PgTableInfoProvider> _logger;

    private readonly ILoggerFactory _loggerFactory;

    private readonly PlatformSchemaResolver _platformResolver;

    public PgTableInfoProvider(
        DatabaseTarget target,
        ILogger<PgTableInfoProvider> logger,
        ILoggerFactory loggerFactory,
        PlatformSchemaResolver platformResolver)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _platformResolver = platformResolver ?? throw new ArgumentNullException(nameof(platformResolver));
    }

    /// <inheritdoc />
    public async Task<List<(string Schema, string Name)>> GetTablesAsync(
        CancellationToken ct)
    {
        try
        {
            var builder = PgConnectionStringBuilder.ParseConnectionString(_target);
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
            var dbName = new NpgsqlConnectionStringBuilder(_target.ConnectionString).Database;
            _logger.LogError(ex, "Failed to get tables from {Db}", dbName);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TableSizeInfo>> GetTableSizesAsync(
        CancellationToken ct)
    {
        try
        {
            var builder = PgConnectionStringBuilder.ParseConnectionString(_target);
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);

            // pg_total_relation_size is an instant catalog lookup (relation size +
            // indexes + TOAST) — safe to run for every table in one batch.
            await using var cmd = new NpgsqlCommand(
                $@"SELECT n.nspname, c.relname, pg_total_relation_size(c.oid)
                  FROM pg_class c
                  JOIN pg_namespace n ON n.oid = c.relnamespace
                  WHERE c.relkind IN ({PgRelKind.TableOrPartition})
                    AND n.nspname NOT IN ({PgSystemSchemas.SqlList})
                    AND n.nspname NOT LIKE 'pg_temp_%'
                  ORDER BY n.nspname, c.relname",
                conn);

            var sizes = new List<TableSizeInfo>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sizes.Add(
                    new TableSizeInfo(
                        new TableId(reader.GetString(0), reader.GetString(1)),
                        reader.GetInt64(2)));
            }

            return sizes;
        }
        catch (Exception ex)
        {
            var dbName = new NpgsqlConnectionStringBuilder(_target.ConnectionString).Database;
            _logger.LogError(ex, "Failed to get table sizes from {Db}", dbName);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<DatabaseModel> ReadDatabaseModelAsync(
        bool excludePlatformSchemas = false,
        CancellationToken ct = default)
    {
        var builder = PgConnectionStringBuilder.ParseConnectionString(_target);
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);

        var resolution = _platformResolver.Resolve(
            "postgresql", builder.Host ?? "localhost", conn.ServerVersion);

        var executor = new PgSqlExecutor(conn, _loggerFactory.CreateLogger<PgSqlExecutor>());
        var reader = new PgMetadataReader(
            executor,
            _loggerFactory.CreateLogger<PgMetadataReader>());
        return await reader.ReadDatabaseModelAsync(excludePlatformSchemas, resolution, ct);
    }
}
