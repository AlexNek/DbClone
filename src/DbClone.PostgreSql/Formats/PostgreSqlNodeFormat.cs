using System.Text.Json;

using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

namespace DbClone.PostgreSql.Formats;

/// <summary>
/// Node.js (pg) JSON config format: { "host": "...", "port": 5432, "database": "...", ... }
/// Export only.
/// </summary>
public sealed class PostgreSqlNodeFormat : IConnectionFormat
{
    public int DetectionPriority => int.MaxValue; // export only

    public string DisplayName => "Node.js (pg)";

    public string Id => "pg-node";

    public EDatabaseProvider Provider => EDatabaseProvider.PostgreSql;

    public string TypicalSource => "JavaScript / Node.js";

    public bool CanExport(DatabaseConnection connection) =>
        connection.Provider == EDatabaseProvider.PostgreSql;

    public bool CanImport(string text) => false;

    public string Export(DatabaseConnection connection)
    {
        var config = new Dictionary<string, object>
                         {
                             ["host"] = connection.Host,
                             ["port"] = connection.Port,
                             ["database"] = connection.Database,
                             ["user"] = connection.Username
                         };

        if (!string.IsNullOrEmpty(connection.Password))
            config["password"] = connection.Password;

        if (connection.SslMode != ESslMode.Disable)
            config["ssl"] = connection.SslMode == ESslMode.Require
                            || connection.SslMode == ESslMode.VerifyCA
                            || connection.SslMode == ESslMode.VerifyFull;

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    public DatabaseConnection Parse(string text) =>
        throw new NotSupportedException("Node.js format is export-only.");
}
