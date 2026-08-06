using System.Text.RegularExpressions;

using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

namespace DbClone.PostgreSql.Formats;

/// <summary>
/// Supabase environment variable format.
/// Detects SUPABASE_URL / VITE_SUPABASE_URL / NEXT_PUBLIC_SUPABASE_URL style env vars
/// and derives a database connection from the project reference in the URL.
/// </summary>
public sealed class PostgreSqlSupabaseEnvFormat : IConnectionFormat
{
    private static readonly string[] KnownVarPrefixes =
        [
            "SUPABASE_URL=",
            "VITE_SUPABASE_URL=",
            "NEXT_PUBLIC_SUPABASE_URL=",
            "REACT_APP_SUPABASE_URL=",
            "NUXT_PUBLIC_SUPABASE_URL=",
            "EXPO_PUBLIC_SUPABASE_URL="
        ];

    private static readonly Regex ProjectRefRegex = new(
        @"https?://([a-z0-9]+)\.supabase\.co",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public int DetectionPriority => 4;

    public string DisplayName => "Supabase (env)";

    public string Id => "pg-supabase-env";

    public EDatabaseProvider Provider => EDatabaseProvider.PostgreSql;

    public string TypicalSource => "React / Next.js / .env";

    public bool CanExport(DatabaseConnection connection) => false;

    public bool CanImport(string text)
    {
        // Single-line or multi-line: must contain a supabase URL variable
        return KnownVarPrefixes.Any(prefix =>
                   text.Contains(prefix, StringComparison.OrdinalIgnoreCase))
               || (text.Contains(".supabase.co", StringComparison.OrdinalIgnoreCase)
                   && text.Contains("SUPABASE_URL=", StringComparison.OrdinalIgnoreCase));
    }

    public string Export(DatabaseConnection connection) =>
        throw new NotSupportedException("Supabase env format is import-only.");

    public DatabaseConnection Parse(string text)
    {
        // Extract the Supabase project URL
        string? projectRef = null;

        var lines = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains("SUPABASE_URL=", StringComparison.OrdinalIgnoreCase))
                continue;

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0) continue;

            var value = trimmed[(eqIndex + 1)..].Trim().Trim('"', '\'');
            var match = ProjectRefRegex.Match(value);
            if (match.Success)
            {
                projectRef = match.Groups[1].Value;
                break;
            }
        }

        if (string.IsNullOrEmpty(projectRef))
            throw new FormatException("Could not extract Supabase project reference from URL.");

        // Construct a direct database connection from the project ref
        var connection = new DatabaseConnection
                             {
                                 Provider = EDatabaseProvider.PostgreSql,
                                 Host = $"db.{projectRef}.supabase.co",
                                 Port = 5432,
                                 Database = "postgres",
                                 Username = "postgres",
                                 Password = null, // not available from env — user must supply
                                 SslMode = ESslMode.Require
                             };
        connection.Options["SupabaseProjectRef"] = projectRef;

        return connection;
    }
}
