namespace DbAdapter.PostgreSql.Metadata;

/// <summary>
/// Splits the <c>SET</c> clauses <c>pg_proc.proconfig</c> records into the settings' names.
/// <para>
/// Each entry is one <c>name=value</c> pair. Only the name is kept: a value can hold a path, an
/// authorization or anything else the source database chose, and nothing downstream needs it —
/// the copy replays the definition text, which already carries the values.
/// </para>
/// </summary>
internal static class PgFunctionSettings
{
    /// <summary>
    /// Returns one name per entry, in the order the server listed them. An empty or missing
    /// array is a function that declares no setting.
    /// </summary>
    public static IReadOnlyList<string> Parse(string[]? configuration)
    {
        if (configuration is null || configuration.Length == 0)
            return [];

        var names = new List<string>(configuration.Length);

        foreach (var entry in configuration)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            // A value may itself contain '=', so the name ends at the first one.
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            var name = (separator < 0 ? entry : entry[..separator]).Trim();

            if (name.Length > 0)
                names.Add(name);
        }

        return names;
    }
}
