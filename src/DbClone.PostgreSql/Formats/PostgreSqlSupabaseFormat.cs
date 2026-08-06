using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

namespace DbClone.PostgreSql.Formats;

/// <summary>
/// Supabase URI format: postgresql://postgres.[project-ref]:[password]@aws-0-[region].pooler.supabase.co:6543/postgres
/// Detected by .supabase.co in the host. Parsing delegates to standard URI logic.
/// </summary>
public sealed class PostgreSqlSupabaseFormat : IConnectionFormat
{
    private readonly PostgreSqlUriFormat _uriFormat = new();

    public int DetectionPriority =>
        5; // must be lower than generic URI (10) so Supabase URIs are detected as Supabase

    public string DisplayName => "Supabase URI";

    public string Id => "pg-supabase";

    public EDatabaseProvider Provider => EDatabaseProvider.PostgreSql;

    public string TypicalSource => "Supabase dashboard";

    public bool CanExport(DatabaseConnection connection) =>
        connection.Provider == EDatabaseProvider.PostgreSql;

    public bool CanImport(string text) =>
        (text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
         || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        && text.Contains(".supabase.co", StringComparison.OrdinalIgnoreCase);

    public string Export(DatabaseConnection connection) => _uriFormat.Export(connection);

    public DatabaseConnection Parse(string text)
    {
        var connection = _uriFormat.Parse(text);

        // Extract project reference from username (e.g. "postgres.myprojectref")
        if (connection.Username.Contains('.'))
        {
            var projectRef = connection.Username[(connection.Username.IndexOf('.') + 1)..];
            connection.Options["SupabaseProjectRef"] = projectRef;
        }

        return connection;
    }
}
