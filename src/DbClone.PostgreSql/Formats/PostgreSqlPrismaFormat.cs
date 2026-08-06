using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

namespace DbClone.PostgreSql.Formats;

/// <summary>
/// Prisma format: postgresql://user:pass@host:port/db?schema=public
/// Export only.
/// </summary>
public sealed class PostgreSqlPrismaFormat : IConnectionFormat
{
    public int DetectionPriority => int.MaxValue; // export only

    public string DisplayName => "Prisma";

    public string Id => "pg-prisma";

    public EDatabaseProvider Provider => EDatabaseProvider.PostgreSql;

    public string TypicalSource => "TypeScript / Node.js";

    public bool CanExport(DatabaseConnection connection) =>
        connection.Provider == EDatabaseProvider.PostgreSql;

    public bool CanImport(string text) => false;

    public string Export(DatabaseConnection connection)
    {
        var userInfo = string.IsNullOrEmpty(connection.Password)
                           ? Uri.EscapeDataString(connection.Username)
                           : $"{Uri.EscapeDataString(connection.Username)}:{Uri.EscapeDataString(connection.Password)}";

        var schema = connection.Options.TryGetValue("schema", out var s) ? s : "public";

        return
            $"postgresql://{userInfo}@{connection.Host}:{connection.Port}/{connection.Database}?schema={Uri.EscapeDataString(schema)}";
    }

    public DatabaseConnection Parse(string text) =>
        throw new NotSupportedException("Prisma format is export-only.");
}
