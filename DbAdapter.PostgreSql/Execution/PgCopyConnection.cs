using System.Data;

using DbAdapter.Definitions;

using Npgsql;

namespace DbAdapter.PostgreSql.Execution;

/// <summary>PostgreSQL implementation of the neutral copy connection handle.</summary>
public sealed class PgCopyConnection : ICopyConnection
{
    private bool _disposed;

    public PgCopyConnection(NpgsqlConnection connection, string connectionString)
    {
        Current = connection ?? throw new ArgumentNullException(nameof(connection));
        ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public NpgsqlConnection Current { get; private set; }

    public string ConnectionString { get; }

    public async ValueTask ReopenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("Cannot reopen a PostgreSQL copy connection without a connection string.");

        try { await Current.DisposeAsync(); }
        catch { }

        var replacement = new NpgsqlConnection(ConnectionString);
        await replacement.OpenAsync(cancellationToken);
        Current = replacement;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await Current.DisposeAsync();
    }

    public static NpgsqlConnection Require(ICopyConnection? connection, string side)
    {
        if (connection is not PgCopyConnection pg)
            throw new InvalidOperationException($"The copy context does not contain a PostgreSQL {side} connection.");

        return pg.Current;
    }
}
