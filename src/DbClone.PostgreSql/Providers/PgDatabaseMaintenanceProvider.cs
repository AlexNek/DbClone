using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Exceptions;
using DbClone.Application.Interfaces;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Providers;

public sealed class PgDatabaseMaintenanceProvider : IDatabaseMaintenanceProvider
{
    /// <summary>
    /// Maximum time (seconds) to wait for locks during destination cleanup.
    /// If DDL commands cannot acquire locks within this period, the operation
    /// fails rather than hanging indefinitely.
    /// </summary>
    private const int CleanLockTimeoutSeconds = 60;

    /// <summary>
    /// Maximum total time (seconds) for any single statement during cleanup.
    /// Guards against long-running CASCADE operations on large databases.
    /// </summary>
    private const int CleanStatementTimeoutSeconds = 300;

    private readonly ILogger<PgDatabaseMaintenanceProvider> _logger;

    public string ProviderName => "PostgreSQL";

    public PgDatabaseMaintenanceProvider(ILogger<PgDatabaseMaintenanceProvider> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> CheckPermissionsAsync(
        ConnectionInfo connection,
        EPermissionCheck checks,
        CancellationToken ct)
    {
        var issues = new List<string>();

        if (checks == EPermissionCheck.None)
            return issues;

        // Connect check — can we reach the target database?
        if (checks.HasFlag(EPermissionCheck.Connect))
        {
            try
            {
                var builder = PgConnectionStringBuilder.BuildConnectionString(connection);
                await using var conn = new NpgsqlConnection(builder.ConnectionString);
                await conn.OpenAsync(ct);

                // Check remaining permissions on this connection
                await CheckRolePermissions(conn, connection, checks, issues, ct);
            }
            catch (Exception ex)
            {
                issues.Add(
                    $"Cannot connect to {connection.Host}:{connection.Port}/{connection.DatabaseName}: {PgExceptionHelper.GetUserMessage(ex)}");
                // If we can't connect, we can't check anything else
                return issues;
            }
        }
        else
        {
            // No connect check requested, but we still need a connection to verify others
            try
            {
                var builder = PgConnectionStringBuilder.BuildConnectionString(connection);
                await using var conn = new NpgsqlConnection(builder.ConnectionString);
                await conn.OpenAsync(ct);
                await CheckRolePermissions(conn, connection, checks, issues, ct);
            }
            catch (Exception ex)
            {
                issues.Add(
                    $"Cannot connect to verify permissions: {PgExceptionHelper.GetUserMessage(ex)}");
            }
        }

        // CreateDatabase requires connecting to 'postgres' maintenance DB
        if (checks.HasFlag(EPermissionCheck.CreateDatabase))
        {
            try
            {
                var builder =
                    PgConnectionStringBuilder.BuildConnectionString(connection, "postgres");

                await using var conn = new NpgsqlConnection(builder.ConnectionString);
                await conn.OpenAsync(ct);

                await using var cmd = new NpgsqlCommand(
                    "SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = current_user",
                    conn);
                var canCreate = await cmd.ExecuteScalarAsync(ct);
                if (canCreate is not true)
                {
                    issues.Add(
                        $"User '{connection.Username}' does not have CREATEDB permission (required for backup mode)");
                }
            }
            catch (Exception ex)
            {
                issues.Add(
                    $"Cannot connect to 'postgres' database for CREATE DATABASE: {PgExceptionHelper.GetUserMessage(ex)}");
            }
        }

        return issues;
    }

    public async Task<bool> CleanDatabaseAsync(
        ConnectionInfo connection,
        Action<string> logMessage,
        CancellationToken ct)
    {
        var dbName = connection.DatabaseName;
        logMessage($"Cleaning target database: {dbName}");

        var builder = PgConnectionStringBuilder.BuildConnectionString(connection);
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);

        // ── Set session-level timeouts to prevent indefinite hangs ───────────
        // lock_timeout: abort if we can't acquire a lock within the limit
        // statement_timeout: abort if any single statement runs too long
        try
        {
            await using var timeoutCmd = new NpgsqlCommand(
                $"SET lock_timeout = '{CleanLockTimeoutSeconds}s'; SET statement_timeout = '{CleanStatementTimeoutSeconds}s';",
                conn);
            timeoutCmd.CommandTimeout = 10;
            await timeoutCmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            logMessage(
                $"Warning: could not set session timeouts: {PgExceptionHelper.GetUserMessage(ex)}");
            _logger.LogWarning(
                ex,
                "Failed to set lock/statement timeout on destination connection");
        }

        // ── Ownership pre-check ─────────────────────────────────────────────
        // DROP SCHEMA requires schema ownership. If the user does not own all
        // non-system schemas (and is not superuser), cleaning is impossible —
        // fail immediately instead of partially cleaning and dooming the copy.
        bool isSuperuser;
        await using (var suCmd = new NpgsqlCommand(
                         "SELECT rolsuper FROM pg_roles WHERE rolname = current_user",
                         conn))
        {
            isSuperuser = await suCmd.ExecuteScalarAsync(ct) is true;
        }

        if (!isSuperuser)
        {
            var foreignSchemas = new List<string>();
            await using var ownerCmd = new NpgsqlCommand(
                $@"SELECT n.nspname FROM pg_namespace n
                  JOIN pg_roles r ON r.oid = n.nspowner
                  WHERE n.nspname NOT IN ({PgSystemSchemas.SqlListWithPublic})
                    AND n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'
                    AND r.rolname <> current_user
                  ORDER BY n.nspname",
                conn);
            await using var reader = await ownerCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                foreignSchemas.Add(reader.GetString(0));

            if (foreignSchemas.Count > 0)
            {
                var schemaList = string.Join(", ", foreignSchemas);
                logMessage(
                    $"ERROR: Cannot clean database '{dbName}': user '{connection.Username}' does not own schema(s): {schemaList}");
                logMessage(
                    "DROP SCHEMA requires schema ownership. Connect as the database owner or a superuser, or use an empty destination database.");
                _logger.LogError(
                    "Clean aborted on {DbName}: user {User} does not own schemas: {Schemas}",
                    dbName,
                    connection.Username,
                    schemaList);
                return false;
            }
        }

        await using var listCmd = new NpgsqlCommand(
            $"SELECT nspname FROM pg_namespace WHERE nspname NOT IN ({PgSystemSchemas.SqlList}) AND nspname NOT LIKE 'pg_temp_%' AND nspname NOT LIKE 'pg_toast_temp_%'",
            conn);

        var schemas = new List<string>();
        await using (var reader = await listCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                schemas.Add(reader.GetString(0));
        }

        foreach (var schema in schemas.Where(s => s != "public"))
        {
            try
            {
                await using var dropCmd = new NpgsqlCommand(
                    $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE",
                    conn);
                await dropCmd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (IsLockOrStatementTimeout(ex))
            {
                LogLockTimeoutError(logMessage, dbName, $"DROP SCHEMA \"{schema}\"", ex);
                return false;
            }
            catch (Exception ex)
            {
                logMessage(
                    $"Warning: could not drop schema {schema}: {PgExceptionHelper.GetUserMessage(ex)}");
            }
        }

        try
        {
            await using var dropViews = new NpgsqlCommand(
                @"DO $$ DECLARE r RECORD; BEGIN
                    FOR r IN SELECT viewname FROM pg_views WHERE schemaname = 'public'
                    LOOP EXECUTE 'DROP VIEW IF EXISTS public.' || quote_ident(r.viewname) || ' CASCADE'; END LOOP;
                END $$;",
                conn);
            await dropViews.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (IsLockOrStatementTimeout(ex))
        {
            LogLockTimeoutError(logMessage, dbName, "DROP VIEWS", ex);
            return false;
        }
        catch (Exception ex)
        {
            logMessage($"Warning: could not drop views: {PgExceptionHelper.GetUserMessage(ex)}");
        }

        try
        {
            await using var dropMatViews = new NpgsqlCommand(
                @"DO $$ DECLARE r RECORD; BEGIN
                    FOR r IN SELECT matviewname FROM pg_matviews WHERE schemaname = 'public'
                    LOOP EXECUTE 'DROP MATERIALIZED VIEW IF EXISTS public.' || quote_ident(r.matviewname) || ' CASCADE'; END LOOP;
                END $$;",
                conn);
            await dropMatViews.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (IsLockOrStatementTimeout(ex))
        {
            LogLockTimeoutError(logMessage, dbName, "DROP MATERIALIZED VIEWS", ex);
            return false;
        }
        catch (Exception ex)
        {
            logMessage(
                $"Warning: could not drop materialized views: {PgExceptionHelper.GetUserMessage(ex)}");
        }

        try
        {
            await using var dropTables = new NpgsqlCommand(
                @"DO $$ DECLARE r RECORD; BEGIN
                    FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'public'
                    LOOP EXECUTE 'DROP TABLE IF EXISTS public.' || quote_ident(r.tablename) || ' CASCADE'; END LOOP;
                END $$;",
                conn);
            await dropTables.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (IsLockOrStatementTimeout(ex))
        {
            LogLockTimeoutError(logMessage, dbName, "DROP TABLES", ex);
            return false;
        }
        catch (Exception ex)
        {
            logMessage($"Warning: could not drop tables: {PgExceptionHelper.GetUserMessage(ex)}");
        }

        try
        {
            await using var dropSeqs = new NpgsqlCommand(
                @"DO $$ DECLARE r RECORD; BEGIN
                    FOR r IN SELECT sequencename FROM pg_sequences WHERE schemaname = 'public'
                    LOOP EXECUTE 'DROP SEQUENCE IF EXISTS public.' || quote_ident(r.sequencename) || ' CASCADE'; END LOOP;
                END $$;",
                conn);
            await dropSeqs.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (IsLockOrStatementTimeout(ex))
        {
            LogLockTimeoutError(logMessage, dbName, "DROP SEQUENCES", ex);
            return false;
        }
        catch (Exception ex)
        {
            logMessage(
                $"Warning: could not drop sequences: {PgExceptionHelper.GetUserMessage(ex)}");
        }

        try
        {
            await using var dropFuncs = new NpgsqlCommand(
                @"DO $$ DECLARE r RECORD; BEGIN
                    FOR r IN SELECT p.oid::regprocedure AS name FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                             WHERE n.nspname = 'public' AND p.prokind IN ('f','p')
                    LOOP EXECUTE 'DROP FUNCTION IF EXISTS ' || r.name || ' CASCADE'; END LOOP;
                END $$;",
                conn);
            await dropFuncs.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (IsLockOrStatementTimeout(ex))
        {
            LogLockTimeoutError(logMessage, dbName, "DROP FUNCTIONS", ex);
            return false;
        }
        catch (Exception ex)
        {
            logMessage(
                $"Warning: could not drop functions: {PgExceptionHelper.GetUserMessage(ex)}");
        }

        try
        {
            await using var dropTypes = new NpgsqlCommand(
                $@"DO $$ DECLARE r RECORD; BEGIN
                    FOR r IN SELECT t.typname FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
                             JOIN pg_class c ON c.oid = t.typrelid
                             WHERE n.nspname = 'public' AND t.typtype = 'c' AND c.relkind = '{PgRelKind.CompositeType}'
                    LOOP EXECUTE 'DROP TYPE IF EXISTS public.' || quote_ident(r.typname) || ' CASCADE'; END LOOP;
                END $$;",
                conn);
            await dropTypes.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (IsLockOrStatementTimeout(ex))
        {
            LogLockTimeoutError(logMessage, dbName, "DROP TYPES", ex);
            return false;
        }
        catch (Exception ex)
        {
            logMessage(
                $"Warning: could not drop composite types: {PgExceptionHelper.GetUserMessage(ex)}");
        }

        try
        {
            await using var dropEnums = new NpgsqlCommand(
                @"DO $$ DECLARE r RECORD; BEGIN
                    FOR r IN SELECT t.typname FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
                             WHERE n.nspname = 'public' AND t.typtype IN ('e','d')
                    LOOP EXECUTE 'DROP TYPE IF EXISTS public.' || quote_ident(r.typname) || ' CASCADE'; END LOOP;
                END $$;",
                conn);
            await dropEnums.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (IsLockOrStatementTimeout(ex))
        {
            LogLockTimeoutError(logMessage, dbName, "DROP ENUMS", ex);
            return false;
        }
        catch (Exception ex)
        {
            logMessage(
                $"Warning: could not drop enums/domains: {PgExceptionHelper.GetUserMessage(ex)}");
        }

        logMessage($"Target database cleaned: {dbName}");
        _logger.LogInformation(
            "Cleaned target database: {DbName} (dropped {Count} non-public schemas, cleaned public)",
            dbName,
            schemas.Count(s => s != "public"));
        return true;
    }

    public async Task<bool> CreateDatabaseAsync(
        ConnectionInfo connection,
        string newDbName,
        Action<string> logMessage,
        CancellationToken ct)
    {
        try
        {
            var builder = PgConnectionStringBuilder.BuildConnectionString(connection, "postgres");

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
                    $"ERROR creating database {newDbName}: user '{connection.Username}' does not have CREATEDB permission");
                _logger.LogError(
                    "User {User} lacks CREATEDB permission on {Host}:{Port}",
                    connection.Username,
                    connection.Host,
                    connection.Port);
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
                connection.Host,
                connection.Port);
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

    public async Task<bool> HasDataAsync(ConnectionInfo connection, CancellationToken ct)
    {
        try
        {
            var builder = PgConnectionStringBuilder.BuildConnectionString(connection);
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

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(
        ConnectionInfo connection,
        CancellationToken ct)
    {
        var databases = new List<string>();
        try
        {
            var builder = PgConnectionStringBuilder.BuildConnectionString(connection, "postgres");

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
                connection.Host,
                connection.Port);
        }

        return databases;
    }

    public async Task<string?> TestConnectionAsync(ConnectionInfo connection, CancellationToken ct)
    {
        var builder = PgConnectionStringBuilder.BuildConnectionString(connection);
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

    private static async Task CheckRolePermissions(
        NpgsqlConnection conn,
        ConnectionInfo connection,
        EPermissionCheck checks,
        List<string> issues,
        CancellationToken ct)
    {
        // Query role attributes and database-level privileges in one shot
        bool isSuperuser, hasCreateDb, canCreateInDb, canConnect;

        await using (var cmd = new NpgsqlCommand(
                         @"SELECT
                r.rolsuper,
                r.rolcreatedb,
                has_database_privilege(current_user, current_database(), 'CREATE') AS can_create_in_db,
                has_database_privilege(current_user, current_database(), 'CONNECT') AS can_connect
              FROM pg_roles r
              WHERE r.rolname = current_user",
                         conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                issues.Add($"Cannot determine permissions for user '{connection.Username}'");
                return;
            }

            isSuperuser = reader.GetBoolean(0);
            hasCreateDb = reader.GetBoolean(1);
            canCreateInDb = reader.GetBoolean(2);
            canConnect = reader.GetBoolean(3);
        }

        // Superuser bypasses all checks
        if (isSuperuser) return;

        if (!canConnect)
        {
            issues.Add(
                $"User '{connection.Username}' does not have CONNECT privilege on database '{connection.DatabaseName}'");
        }

        if (checks.HasFlag(EPermissionCheck.CreateObjects) && !canCreateInDb)
        {
            issues.Add(
                $"User '{connection.Username}' does not have CREATE privilege on database '{connection.DatabaseName}' (cannot create schemas/tables)");
        }

        if (checks.HasFlag(EPermissionCheck.DropObjects))
        {
            // DROP SCHEMA requires schema ownership. Detect schemas owned by other
            // roles — cleaning will be refused later, so report it now.
            var foreignSchemas = new List<string>();
            await using (var schemaCmd = new NpgsqlCommand(
                             $@"SELECT n.nspname FROM pg_namespace n
                  JOIN pg_roles r ON r.oid = n.nspowner
                  WHERE n.nspname NOT IN ({PgSystemSchemas.SqlListWithPublic})
                    AND n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'
                    AND r.rolname <> current_user
                  ORDER BY n.nspname",
                             conn))
            await using (var reader = await schemaCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    foreignSchemas.Add(reader.GetString(0));
            }

            if (foreignSchemas.Count > 0)
            {
                issues.Add(
                    $"User '{connection.Username}' does not own schema(s) in '{connection.DatabaseName}': "
                    +
                    $"{string.Join(", ", foreignSchemas)}. Cleaning the database requires schema ownership — "
                    +
                    "connect as the database owner or a superuser, or use an empty destination database.");
            }
        }
    }

    /// <summary>
    /// Detects PostgreSQL lock_timeout (55P03) and statement_timeout (57014) errors.
    /// </summary>
    private static bool IsLockOrStatementTimeout(PostgresException ex) =>
        ex.SqlState == "55P03" || ex.SqlState == "57014";

    /// <summary>
    /// Logs a user-friendly error explaining why the destination cleanup failed due to locks.
    /// </summary>
    private void LogLockTimeoutError(
        Action<string> logMessage,
        string dbName,
        string operation,
        PostgresException ex)
    {
        var isLock = ex.SqlState == "55P03";
        var reason = isLock
                         ? "Another connection is holding a lock on objects in the destination database."
                         : "The operation took too long (possible lock contention or very large CASCADE).";

        logMessage($"ERROR: Cannot clean database '{dbName}' — timed out during {operation}.");
        logMessage($"Reason: {reason}");
        logMessage(
            "To fix: close all other connections to the destination database (pgAdmin, DBeaver, other DbClone instances, application pools) and retry.");
        logMessage(
            "Hint: You can check active connections with: SELECT * FROM pg_stat_activity WHERE datname = '<dbname>';");

        _logger.LogError(
            ex,
            "Clean aborted on {DbName}: {Operation} hit {TimeoutType} (SqlState={SqlState})",
            dbName,
            operation,
            isLock ? "lock_timeout" : "statement_timeout",
            ex.SqlState);
    }
}
