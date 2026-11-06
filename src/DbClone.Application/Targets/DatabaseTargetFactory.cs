using DbAdapter.Definitions;
using CanonicalModel.DTOs;
using CanonicalModel.Enums;
using DbClone.Application.Adapters;
using DbClone.Application.DTOs;

namespace DbClone.Application.Targets;

/// <summary>
/// The one place that turns a connection into the shape the engine boundary accepts, and the
/// place that resolves which installed adapter serves it. The text is written by the adapter's
/// own syntax, so no application file ever spells an engine's connection-string format; the
/// adapter comes from the catalog by the connection's engine value (refactors/09 P4).
/// </summary>
public sealed class DatabaseTargetFactory
{
    private readonly IAdapterCatalog _catalog;

    /// <summary>Initializes a new instance.</summary>
    public DatabaseTargetFactory(IAdapterCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>
    /// The adapter that serves a connection whose engine value is <paramref name="engine"/>,
    /// applying the single selection rule. Other services resolve the adapter here so the rule
    /// lives in one place.
    /// </summary>
    public IDatabaseAdapter ResolveAdapter(string? engine) => _catalog.Resolve(engine);

    /// <summary>Builds the target the given connection points at.</summary>
    public DatabaseTarget Create(ConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var adapter = _catalog.Resolve(connection.Engine);
        var dialect = adapter.Info.Dialect;
        var fields = new ConnectionStringFields(
            Host: connection.Host,
            Port: connection.Port,
            Database: connection.DatabaseName,
            Username: connection.Username,
            Password: connection.Password,
            SslMode: adapter.Syntax.ToSslMode(connection.SslMode),
            Provider: dialect);

        return new DatabaseTarget(dialect, adapter.Syntax.BuildKeyValue(fields));
    }
}
