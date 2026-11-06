using DbAdapter.Definitions;
using CanonicalModel.DTOs;
using CanonicalModel.Enums;
using DbClone.Application.DTOs;

namespace DbClone.Application.Targets;

/// <summary>
/// The one place that turns a connection into the shape the engine boundary accepts.
/// The text is written by the adapter's own syntax, so no application file ever spells
/// an engine's connection-string format.
/// </summary>
public sealed class DatabaseTargetFactory
{
    private readonly IDatabaseAdapter _adapter;

    /// <summary>Initializes a new instance.</summary>
    public DatabaseTargetFactory(IDatabaseAdapter adapter) =>
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    /// <summary>Builds the target the given connection points at.</summary>
    public DatabaseTarget Create(ConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var dialect = _adapter.Info.Dialect;
        var fields = new ConnectionStringFields(
            Host: connection.Host,
            Port: connection.Port,
            Database: connection.DatabaseName,
            Username: connection.Username,
            Password: connection.Password,
            SslMode: connection.SslMode,
            Provider: ProviderOf(dialect));

        return new DatabaseTarget(dialect, _adapter.Syntax.BuildKeyValue(fields));
    }

    private static EDatabaseProvider ProviderOf(string dialect) =>
        Enum.TryParse<EDatabaseProvider>(dialect, ignoreCase: true, out var provider)
            ? provider
            : throw new NotSupportedException(
                $"No known provider matches the adapter dialect '{dialect}'.");
}
