using DbAdapter.Definitions;
using DbClone.Application.DTOs;
using DbClone.Application.Interfaces;
using DbClone.Application.Targets;

namespace DbClone.Application.Services;

/// <summary>
/// Answers the server-fact questions the connection form asks, delegating to the adapter's
/// admin role for the target each call names (D5, D9). Nothing destructive reaches this class.
/// </summary>
public sealed class ServerAdminService : IServerAdminService
{
    private readonly IDatabaseAdapter _adapter;
    private readonly DatabaseTargetFactory _targetFactory;

    public ServerAdminService(IDatabaseAdapter adapter, DatabaseTargetFactory targetFactory)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _targetFactory = targetFactory ?? throw new ArgumentNullException(nameof(targetFactory));
    }

    /// <inheritdoc />
    public string GetProviderName(ConnectionInfo connection) =>
        _adapter.CreateAdmin(_targetFactory.Create(connection)).ProviderName;

    /// <inheritdoc />
    public Task<string?> TestConnectionAsync(ConnectionInfo connection, CancellationToken ct) =>
        _adapter.CreateAdmin(_targetFactory.Create(connection)).TestConnectionAsync(ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionInfo connection, CancellationToken ct) =>
        _adapter.CreateAdmin(_targetFactory.Create(connection)).ListDatabasesAsync(ct);
}
