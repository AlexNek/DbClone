using DbAdapter.Definitions;
using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbAdapter.PostgreSql.Formats;

/// <summary>
/// Prisma format: postgresql://user:pass@host:port/db?schema=public
/// Export only.
/// </summary>
public sealed class PostgreSqlPrismaFormat : IConnectionTextFormat
{
    public int DetectionPriority => int.MaxValue; // export only

    public string DisplayName => "Prisma";

    public string Id => "pg-prisma";

    public string Provider => "postgresql";

    public string TypicalSource => "TypeScript / Node.js";

    public bool CanExport(ConnectionStringFields fields) =>
        fields.Provider == "postgresql";

    public bool CanImport(string text) => false;

    public string Export(ConnectionStringFields fields)
    {
        var userInfo = string.IsNullOrEmpty(fields.Password)
                           ? Uri.EscapeDataString(fields.Username)
                           : $"{Uri.EscapeDataString(fields.Username)}:{Uri.EscapeDataString(fields.Password)}";

        var schema = fields.Options is not null && fields.Options.TryGetValue("schema", out var s)
                         ? s
                         : "public";

        return
            $"postgresql://{userInfo}@{fields.Host}:{fields.Port}/{fields.Database}?schema={Uri.EscapeDataString(schema)}";
    }

    public ConnectionStringFields Parse(string text) =>
        throw new NotSupportedException("Prisma format is export-only.");
}
