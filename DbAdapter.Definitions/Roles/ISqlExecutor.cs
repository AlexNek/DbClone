namespace DbAdapter.Definitions;

/// <summary>
/// Executes statements with retry, timeout, logging, cancellation, and transaction support.
/// </summary>
public interface ISqlExecutor
{
    /// <summary>
    /// Executes multiple statements within a transaction.
    /// </summary>
    Task ExecuteInTransactionAsync(
        Func<ISqlExecutor, Task> action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a non-query statement.
    /// </summary>
    Task<int> ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a statement and returns a scalar value.
    /// </summary>
    Task<T> ExecuteScalarAsync<T>(string sql, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a statement and returns rows.
    /// </summary>
    Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Func<System.Data.Common.DbDataReader, T> mapper,
        CancellationToken cancellationToken = default);
}
