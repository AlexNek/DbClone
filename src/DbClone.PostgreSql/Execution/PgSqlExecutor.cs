using System.Data.Common;

using DbClone.Application.Interfaces;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Execution;

/// <summary>
/// PostgreSQL implementation of <see cref="ISqlExecutor"/> with retry logic and logging.
/// </summary>
public sealed class PgSqlExecutor : ISqlExecutor, IAsyncDisposable
{
    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(30);

    private readonly NpgsqlConnection _connection;

    private readonly ILogger _logger;

    private readonly int _maxRetries = 3;

    private readonly TimeSpan[] _retryDelays =
        [
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2)
        ];

    /// <summary>
    /// Gets the underlying Npgsql connection.
    /// </summary>
    public NpgsqlConnection Connection => _connection;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgSqlExecutor"/> class.
    /// </summary>
    public PgSqlExecutor(NpgsqlConnection connection, ILogger logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PgSqlExecutor"/> class with custom timeout.
    /// </summary>
    public PgSqlExecutor(NpgsqlConnection connection, ILogger logger, TimeSpan commandTimeout)
        : this(connection, logger)
    {
        _commandTimeout = commandTimeout;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection.State == System.Data.ConnectionState.Open)
        {
            await _connection.CloseAsync();
        }
    }

    /// <summary>
    /// Ensures the underlying connection is open.
    /// </summary>
    public async Task EnsureOpenAsync(CancellationToken cancellationToken = default)
    {
        if (_connection.State == System.Data.ConnectionState.Open)
            return;

        _logger.LogDebug("Opening connection to {Database}", _connection.Database);
        await _connection.OpenAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ExecuteInTransactionAsync(
        Func<ISqlExecutor, Task> action,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken);
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
        var txExecutor = new PgSqlExecutor(_connection, _logger, _commandTimeout);
        try
        {
            await action(txExecutor);
            await transaction.CommitAsync(cancellationToken);
            _logger.LogDebug("Transaction committed");
        }
        catch
        {
            _logger.LogWarning("Transaction rolling back");
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<int> ExecuteNonQueryAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        return await RetryAsync(
                   async () =>
                       {
                           await EnsureOpenAsync(cancellationToken);
                           await using var cmd = new NpgsqlCommand(sql, _connection)
                                                     {
                                                         CommandTimeout =
                                                             (int)_commandTimeout.TotalSeconds
                                                     };
                           _logger.LogDebug("ExecuteNonQuery: {Sql}", TruncateSql(sql));
                           var result = await cmd.ExecuteNonQueryAsync(cancellationToken);
                           return result;
                       },
                   "ExecuteNonQueryAsync",
                   cancellationToken);
    }

    /// <inheritdoc />
    public async Task<T> ExecuteScalarAsync<T>(
        string sql,
        CancellationToken cancellationToken = default)
    {
        return await RetryAsync(
                   async () =>
                       {
                           await EnsureOpenAsync(cancellationToken);
                           await using var cmd = new NpgsqlCommand(sql, _connection)
                                                     {
                                                         CommandTimeout =
                                                             (int)_commandTimeout.TotalSeconds
                                                     };
                           _logger.LogDebug("ExecuteScalar: {Sql}", TruncateSql(sql));
                           var result = await cmd.ExecuteScalarAsync(cancellationToken);
                           if (result is null || result is DBNull)
                               throw new InvalidOperationException("Scalar query returned null");
                           return (T)Convert.ChangeType(result, typeof(T))!;
                       },
                   "ExecuteScalarAsync",
                   cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Func<DbDataReader, T> mapper,
        CancellationToken cancellationToken = default)
    {
        return await RetryAsync(
                   async () =>
                       {
                           await EnsureOpenAsync(cancellationToken);
                           await using var cmd = new NpgsqlCommand(sql, _connection)
                                                     {
                                                         CommandTimeout =
                                                             (int)_commandTimeout.TotalSeconds
                                                     };
                           _logger.LogDebug("Query: {Sql}", TruncateSql(sql));
                           await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                           var results = new List<T>();
                           while (await reader.ReadAsync(cancellationToken))
                           {
                               results.Add(mapper(reader));
                           }

                           return (IReadOnlyList<T>)results;
                       },
                   "QueryAsync",
                   cancellationToken);
    }

    private static bool IsTransient(NpgsqlException ex)
    {
        // Transient error codes:
        // 40001 = serialization_failure
        // 40P01 = deadlock_detected
        // 08xxx = connection_exception
        // 53xxx = insufficient_resources
        if (ex.SqlState is "40001" or "40P01")
            return true;

        if (ex.SqlState is not null && ex.SqlState.StartsWith("08"))
            return true;

        if (ex.SqlState is not null && ex.SqlState.StartsWith("53"))
            return true;

        // Npgsql connection issues
        if (ex.InnerException is TimeoutException or IOException)
            return true;

        return false;
    }

    private async Task<T> RetryAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (NpgsqlException ex) when (attempt < _maxRetries && IsTransient(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Transient error on attempt {Attempt}/{MaxRetries} in {Operation}. Retrying in {Delay}ms",
                    attempt + 1,
                    _maxRetries,
                    operationName,
                    _retryDelays[attempt].TotalMilliseconds);
                await Task.Delay(_retryDelays[attempt], cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in {Operation}: {Message}", operationName, ex.Message);
                throw;
            }
        }

        throw new InvalidOperationException("Retry logic exhausted");
    }

    private static string TruncateSql(string sql) => sql.Length > 200 ? sql[..200] + "..." : sql;
}
