namespace DbAdapter.PostgreSql;

/// <summary>
/// The extensions every PostgreSQL database already carries, so the check that refuses a restore and
/// the clean that prepares a target agree on what the target's user content is.
/// </summary>
/// <remarks>
/// Written once because both rules read it: the restore refuses a target holding an extension outside
/// this set, and the clean drops every extension outside it. Two copies of the list are how a target
/// can keep one of them through the clean and then refuse the restore the user asked for.
/// <c>plpgsql</c> is the only one — it is installed by default, and dropping it would remove the
/// language the catalog's functions are written in.
/// </remarks>
public static class PgBuiltInExtensions
{
    /// <summary>Extension names every database already has.</summary>
    public static readonly string[] All = ["plpgsql"];

    /// <summary>
    /// Pre-formatted SQL IN-clause list for direct embedding in queries.
    /// Produces: 'plpgsql'
    /// </summary>
    public static readonly string SqlList = string.Join(", ", All.Select(s => $"'{s}'"));
}
