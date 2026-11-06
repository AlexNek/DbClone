
using Microsoft.Extensions.Logging;

using Npgsql;
using DbAdapter.Definitions;

namespace DbAdapter.PostgreSql.Execution;

/// <summary>
/// Default implementation that delegates to <see cref="PgConnectionHelper"/>.
/// </summary>
public sealed class PgConnectionProvider : IPgConnectionProvider
{
    /// <inheritdoc />
    public Task<NpgsqlConnection?> GetDestinationConnectionAsync(
        CopyContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        return PgConnectionHelper.ValidateAndReopenAsync(
            context,
            isSource: false,
            logger,
            cancellationToken);
    }
}
