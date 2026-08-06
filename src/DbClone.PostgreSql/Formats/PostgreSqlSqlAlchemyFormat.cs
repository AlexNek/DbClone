using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

namespace DbClone.PostgreSql.Formats;

/// <summary>
/// SQLAlchemy (Python) format: postgresql+psycopg2://user:pass@host:port/db
/// Export only.
/// </summary>
public sealed class PostgreSqlSqlAlchemyFormat : IConnectionFormat
{
    public int DetectionPriority => int.MaxValue; // export only, never detected

    public string DisplayName => "SQLAlchemy (Python)";

    public string Id => "pg-sqlalchemy";

    public EDatabaseProvider Provider => EDatabaseProvider.PostgreSql;

    public string TypicalSource => "Python";

    public bool CanExport(DatabaseConnection connection) =>
        connection.Provider == EDatabaseProvider.PostgreSql;

    public bool CanImport(string text) => false;

    public string Export(DatabaseConnection connection)
    {
        var userInfo = string.IsNullOrEmpty(connection.Password)
                           ? Uri.EscapeDataString(connection.Username)
                           : $"{Uri.EscapeDataString(connection.Username)}:{Uri.EscapeDataString(connection.Password)}";

        return
            $"postgresql+psycopg2://{userInfo}@{connection.Host}:{connection.Port}/{connection.Database}";
    }

    public DatabaseConnection Parse(string text) =>
        throw new NotSupportedException("SQLAlchemy format is export-only.");
}
