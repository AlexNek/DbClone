using System.Text.Json;

using DbAdapter.Definitions;
using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbAdapter.PostgreSql.Formats;

/// <summary>
/// Node.js (pg) JSON config format: { "host": "...", "port": 5432, "database": "...", ... }
/// Export only.
/// </summary>
public sealed class PostgreSqlNodeFormat : IConnectionTextFormat
{
    public int DetectionPriority => int.MaxValue; // export only

    public string DisplayName => "Node.js (pg)";

    public string Id => "pg-node";

    public string Provider => "postgresql";

    public string TypicalSource => "JavaScript / Node.js";

    public bool CanExport(ConnectionStringFields fields) =>
        fields.Provider == "postgresql";

    public bool CanImport(string text) => false;

    public string Export(ConnectionStringFields fields)
    {
        var config = new Dictionary<string, object>
                         {
                             ["host"] = fields.Host,
                             ["port"] = fields.Port,
                             ["database"] = fields.Database,
                             ["user"] = fields.Username
                         };

        if (!string.IsNullOrEmpty(fields.Password))
            config["password"] = fields.Password;

        if (fields.SslMode != ESslMode.Disable)
            config["ssl"] = fields.SslMode == ESslMode.Require
                            || fields.SslMode == ESslMode.VerifyCA
                            || fields.SslMode == ESslMode.VerifyFull;

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    public ConnectionStringFields Parse(string text) =>
        throw new NotSupportedException("Node.js format is export-only.");
}
