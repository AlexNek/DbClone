using DbAdapter.Definitions;
using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbAdapter.PostgreSql.Formats;

/// <summary>
/// SQLAlchemy (Python) format: postgresql+psycopg2://user:pass@host:port/db
/// Export only.
/// </summary>
public sealed class PostgreSqlSqlAlchemyFormat : IConnectionTextFormat
{
    public int DetectionPriority => int.MaxValue; // export only, never detected

    public string DisplayName => "SQLAlchemy (Python)";

    public string Id => "pg-sqlalchemy";

    public string Provider => "postgresql";

    public string TypicalSource => "Python";

    public bool CanExport(ConnectionStringFields fields) =>
        fields.Provider == "postgresql";

    public bool CanImport(string text) => false;

    public string Export(ConnectionStringFields fields)
    {
        var userInfo = string.IsNullOrEmpty(fields.Password)
                           ? Uri.EscapeDataString(fields.Username)
                           : $"{Uri.EscapeDataString(fields.Username)}:{Uri.EscapeDataString(fields.Password)}";

        return
            $"postgresql+psycopg2://{userInfo}@{fields.Host}:{fields.Port}/{fields.Database}";
    }

    public ConnectionStringFields Parse(string text) =>
        throw new NotSupportedException("SQLAlchemy format is export-only.");
}
