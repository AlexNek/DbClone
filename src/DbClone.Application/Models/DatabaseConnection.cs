using DbClone.Application.Enums;

namespace DbClone.Application.Models;

/// <summary>
/// Provider-neutral representation of a database connection.
/// Used as the interchange model between import/export formats and the UI persistence layer.
/// </summary>
public sealed class DatabaseConnection
{
    public string Database { get; set; } = string.Empty;

    public string Host { get; set; } = "localhost";

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Provider-specific options not represented by the common model.
    /// Examples: Connection Timeout, Application Name, Pooling, Search Path, Load Balance Hosts.
    /// </summary>
    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Password { get; set; }

    public int Port { get; set; } = 5432;

    public EDatabaseProvider Provider { get; set; }

    public ESslMode SslMode { get; set; } = ESslMode.Prefer;

    public string Username { get; set; } = string.Empty;
}
