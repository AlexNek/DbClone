using DbAdapter.Definitions;
using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbAdapter.PostgreSql.Formats;

/// <summary>
/// Supabase URI format: postgresql://postgres.[project-ref]:[password]@aws-0-[region].pooler.supabase.co:6543/postgres
/// Detected by .supabase.co in the host. Parsing delegates to standard URI logic.
/// </summary>
public sealed class PostgreSqlSupabaseFormat : IConnectionTextFormat
{
    private readonly PostgreSqlUriFormat _uriFormat = new();

    public int DetectionPriority =>
        5; // must be lower than generic URI (10) so Supabase URIs are detected as Supabase

    public string DisplayName => "Supabase URI";

    public string Id => "pg-supabase";

    public EDatabaseProvider Provider => EDatabaseProvider.PostgreSql;

    public string TypicalSource => "Supabase dashboard";

    public bool CanExport(ConnectionStringFields fields) =>
        fields.Provider == EDatabaseProvider.PostgreSql;

    public bool CanImport(string text) =>
        (text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
         || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        && text.Contains(".supabase.co", StringComparison.OrdinalIgnoreCase);

    public string Export(ConnectionStringFields fields) => _uriFormat.Export(fields);

    public ConnectionStringFields Parse(string text)
    {
        var fields = _uriFormat.Parse(text);

        // Extract project reference from username (e.g. "postgres.myprojectref")
        if (!fields.Username.Contains('.'))
            return fields;

        var options = new Dictionary<string, string>(
            fields.Options ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        options["SupabaseProjectRef"] = fields.Username[(fields.Username.IndexOf('.') + 1)..];

        return fields with { Options = options };
    }
}
