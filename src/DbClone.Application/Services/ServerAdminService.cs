using DbAdapter.Definitions;
using DbClone.Application.DTOs;
using DbClone.Application.Interfaces;
using DbClone.Application.Targets;

namespace DbClone.Application.Services;

/// <summary>
/// Answers the server-fact questions the connection form asks, delegating to the admin role of
/// the adapter that serves the target each call names (D5, D9). Nothing destructive reaches this
/// class. The adapter is resolved by the connection's engine value, never named here.
/// </summary>
public sealed class ServerAdminService : IServerAdminService
{
    private readonly DatabaseTargetFactory _targetFactory;

    public ServerAdminService(DatabaseTargetFactory targetFactory) =>
        _targetFactory = targetFactory ?? throw new ArgumentNullException(nameof(targetFactory));

    private IDatabaseAdmin AdminFor(ConnectionInfo connection) =>
        _targetFactory.ResolveAdapter(connection.Engine).CreateAdmin(_targetFactory.Create(connection));

    /// <inheritdoc />
    public string GetProviderName(ConnectionInfo connection) => AdminFor(connection).ProviderName;

    /// <inheritdoc />
    public Task<string?> TestConnectionAsync(ConnectionInfo connection, CancellationToken ct) =>
        AdminFor(connection).TestConnectionAsync(ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionInfo connection, CancellationToken ct) =>
        AdminFor(connection).ListDatabasesAsync(ct);
}
