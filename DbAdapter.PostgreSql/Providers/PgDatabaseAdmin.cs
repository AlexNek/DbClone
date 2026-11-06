using DbAdapter.Definitions;

using Microsoft.Extensions.Logging;

using Npgsql;
using CanonicalModel.Exceptions;

namespace DbAdapter.PostgreSql.Providers;

/// <summary>
/// The administration role: the server facts the connection form asks for and
/// database creation, answered for the one target the adapter created this
/// instance for. Nothing here is destructive.
/// </summary>
public sealed class PgDatabaseAdmin : IDatabaseAdmin
{
    private readonly DatabaseTarget _target;

    private readonly ILogger<PgDatabaseAdmin> _logger;

    public string ProviderName => "PostgreSQL";

    public PgDatabaseAdmin(DatabaseTarget target, ILogger<PgDatabaseAdmin> logger)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> TestConnectionAsync(CancellationToken ct)
    {
        var builder = PgConnectionStringBuilder.ParseConnectionString(_target);
        try
        {
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);
            return conn.ServerVersion;
        }
        catch (PostgresException ex)
        {
            throw new DatabaseConnectionException(ex.MessageText, ex.SqlState, ex);
        }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken ct)
    {
        var builder = PgConnectionStringBuilder.ParseConnectionString(_target);
        builder.Database = "postgres";

        var databases = new List<string>();
        try
        {
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(
                "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname",
                conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                databases.Add(reader.GetString(0));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to list databases on {Host}:{Port}",
                builder.Host,
                builder.Port);
        }

        return databases;
    }

    public async Task<bool> HasDataAsync(CancellationToken ct)
    {
        try
        {
            var builder = PgConnectionStringBuilder.ParseConnectionString(_target);
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(
                $@"SELECT (
                    (SELECT COUNT(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                     WHERE n.nspname NOT IN ({PgSystemSchemas.SqlList})
                       AND n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'
                       AND c.relkind IN ({PgRelKind.AllUserRelations},'{PgRelKind.Sequence}'))
                    + (SELECT COUNT(*) FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
                       JOIN pg_class c ON c.oid = t.typrelid
                       WHERE n.nspname NOT IN ({PgSystemSchemas.SqlList})
                       AND t.typtype = 'c' AND c.relkind = '{PgRelKind.CompositeType}')
                    + (SELECT COUNT(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                       WHERE n.nspname NOT IN ({PgSystemSchemas.SqlList}))
                )",
                conn);
            cmd.CommandTimeout = 60; // Fail fast if catalog is locked
            var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
            return count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check if destination has data, assuming it does");
            return true;
        }
    }

    public async Task<bool> CreateDatabaseAsync(
        string newDbName,
        Action<string> logMessage,
        CancellationToken ct)
    {
        try
        {
            var builder = PgConnectionStringBuilder.ParseConnectionString(_target);
            builder.Database = "postgres";

            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);

            // Check if the user has CREATEDB permission
            await using var permCmd = new NpgsqlCommand(
                "SELECT rolcreatedb FROM pg_roles WHERE rolname = current_user",
                conn);
            var canCreate = await permCmd.ExecuteScalarAsync(ct);
            if (canCreate is not true)
            {
                logMessage(
                    $"ERROR creating database {newDbName}: user '{builder.Username}' does not have CREATEDB permission");
                _logger.LogError(
                    "User {User} lacks CREATEDB permission on {Host}:{Port}",
                    builder.Username,
                    builder.Host,
                    builder.Port);
                return false;
            }

            await using var checkCmd = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = @dbName",
                conn);
            checkCmd.Parameters.AddWithValue("dbName", newDbName);
            var exists = await checkCmd.ExecuteScalarAsync(ct) != null;

            if (exists)
            {
                // A backup must always be a fresh, faithful copy. If the database
                // exists (e.g. from a previous failed attempt), drop and recreate.
                logMessage($"Database {newDbName} already exists — dropping for fresh backup");
                await using var dropCmd = new NpgsqlCommand(
                    $"DROP DATABASE \"{newDbName}\"",
                    conn);
                await dropCmd.ExecuteNonQueryAsync(ct);
            }

            // Backup mode creates an empty database from template0 so that nothing
            // is inherited from the hosting platform's template1 (e.g. Supabase
            // provisions auth, storage, realtime into template1). The copy pipeline
            // then faithfully reproduces the source: platform schemas are copied if
            // the source has them (CopyPlatformSchemas), and CreateSchemasStage
            // verifies system schema presence (pg_catalog, information_schema).
            await using var createCmd = new NpgsqlCommand(
                $"CREATE DATABASE \"{newDbName}\" TEMPLATE template0",
                conn);
            await createCmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (NpgsqlException ex) when (ex.Message.Contains("does not exist")
                                         || ex.Message.Contains("password authentication failed")
                                         || ex.Message.Contains("no pg_hba.conf entry"))
        {
            logMessage(
                $"ERROR creating database {newDbName}: cannot connect to maintenance database 'postgres' — {PgExceptionHelper.GetUserMessage(ex)}");
            _logger.LogError(
                ex,
                "Cannot connect to 'postgres' database on {Host}:{Port} for CREATE DATABASE",
                HostOf(),
                PortOf());
            return false;
        }
        catch (Exception ex)
        {
            logMessage(
                $"ERROR creating database {newDbName}: {PgExceptionHelper.GetUserMessage(ex)}");
            _logger.LogError(ex, "Failed to create database {DbName}", newDbName);
            return false;
        }
    }

    /// <inheritdoc />
    public string BuildBackupDatabaseName(string rawPrefix)
    {
        if (string.IsNullOrEmpty(rawPrefix))
            return "db";

        var sb = new System.Text.StringBuilder();
        foreach (var c in rawPrefix)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(char.ToLowerInvariant(c));
        }

        var result = sb.ToString();
        if (string.IsNullOrEmpty(result))
            return "db";

        // PostgreSQL identifiers cannot start with a digit
        if (char.IsDigit(result[0]))
            result = "db_" + result;

        return result.Length > 63 ? result[..63] : result;
    }

    private string HostOf() =>
        new NpgsqlConnectionStringBuilder(_target.ConnectionString).Host ?? string.Empty;

    private int PortOf() =>
        new NpgsqlConnectionStringBuilder(_target.ConnectionString).Port;
}
