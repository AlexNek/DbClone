using DbClone.Application.Interfaces;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Execution;

/// <summary>
/// Creates <see cref="ISqlExecutor"/> instances bound to a database connection.
/// Abstraction point that lets pipeline stages be unit-tested without a live database.
/// </summary>
public interface IPgExecutorFactory
{
    /// <summary>
    /// Creates an executor bound to the given connection.
    /// </summary>
    ISqlExecutor Create(NpgsqlConnection connection);
}

/// <summary>
/// Default implementation producing <see cref="PgSqlExecutor"/> instances.
/// </summary>
public sealed class PgExecutorFactory : IPgExecutorFactory
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Initializes a new instance.</summary>
    public PgExecutorFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public ISqlExecutor Create(NpgsqlConnection connection) =>
        new PgSqlExecutor(connection, _loggerFactory.CreateLogger<PgSqlExecutor>());
}
