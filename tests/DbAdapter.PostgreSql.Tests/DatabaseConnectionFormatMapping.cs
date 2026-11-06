using CanonicalModel.DTOs;
using DbClone.Application.Models;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// The text formats read and write <see cref="ConnectionStringFields"/>, while these tests
/// keep describing a stored connection — this maps one shape onto the other.
/// </summary>
internal static class DatabaseConnectionFormatMapping
{
    public static ConnectionStringFields ToFields(this DatabaseConnection connection) => new(
        Host: connection.Host,
        Port: connection.Port,
        Database: connection.Database,
        Username: connection.Username,
        Password: connection.Password ?? string.Empty,
        SslMode: connection.SslMode,
        Provider: connection.Provider,
        Options: connection.Options.Count > 0
            ? new Dictionary<string, string>(connection.Options)
            : null);
}
