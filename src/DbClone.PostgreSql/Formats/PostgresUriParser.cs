using System.Globalization;
using System.Text.RegularExpressions;

namespace DbClone.PostgreSql.Formats;

/// <summary>
/// Parses PostgreSQL connection URIs with tolerance for unencoded special characters
/// in passwords (<c>@</c>, <c>#</c>, <c>:</c>).
/// <para>
/// Strategy: score every <c>@</c> whose right-hand side looks like a valid host
/// (right-to-left scan) and pick the best reading. Rich host candidates (dotted
/// name, explicit port, database path) beat bare ones, and user-info that swallowed
/// host- or query-like structure is penalized. Rightmost wins on ties, mirroring
/// what <c>libpq</c> / <c>psql</c> do in practice.
/// </para>
/// </summary>
internal static partial class PostgresUriParser
{
    private const int DefaultPort = 5432;

    private const char FragmentSeparator = '#';

    private const char IPv6Close = ']';

    private const char IPv6Open = '[';

    private const char PasswordSeparator = ':';

    private const char PathSeparator = '/';

    private const char PercentSign = '%';

    private const string PrefixPostgres = SchemePostgres + SchemeSuffixSeparator;

    private const string PrefixPostgresql = SchemePostgresql + SchemeSuffixSeparator;

    private const char QueryKeyValueSeparator = '=';

    private const char QueryPairSeparator = '&';

    private const char QuerySeparator = '?';

    private const string SchemePostgres = "postgres";

    private const string SchemePostgresql = "postgresql";

    private const string SchemeSuffixSeparator = "://";

    private const char UserInfoHostSeparator = '@';

    public static ParsedUri? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var schemeResult = StripScheme(text);
        if (schemeResult is null)
        {
            return null;
        }

        var (scheme, remainder) = schemeResult.Value;
        var (userInfoRaw, hostAndRest) = SplitUserInfoFromHost(remainder);
        var (hostPortDb, queryString) = SplitHostAndQuery(hostAndRest);

        var hostResult = ParseHostPortDatabase(hostPortDb);
        if (hostResult is null)
        {
            return null;
        }

        var (host, port, database) = hostResult.Value;
        var (username, password) = ParseUserInfo(userInfoRaw);
        var queryParams = ParseQueryString(queryString);

        return new ParsedUri(scheme, host, port, database, username, password, queryParams);
    }

    /// <summary>
    /// Decodes a percent-encoded component, or returns the raw value as-is.
    /// Malformed percent sequences are treated as literals.
    /// </summary>
    private static string DecodeComponent(string raw)
    {
        if (!raw.Contains(PercentSign))
        {
            return raw;
        }

        try
        {
            return Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            return raw;
        }
    }

    [GeneratedRegex(
        @"^(?<host>\[[^\]]+\]|[a-zA-Z0-9\.\-]+)(:(?<port>\d+))?(?<rest>[/?].*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex HostPortPattern();

    /// <summary>
    /// Detects host-with-port remnants (<c>name:1234</c>) inside the user-info,
    /// a strong signal that the split swallowed the real host into the password.
    /// </summary>
    [GeneratedRegex(
        @"[a-zA-Z0-9\.\-]+:\d{1,5}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedHostPortPattern();

    private static bool IsValidHostStart(string candidate) =>
        !string.IsNullOrEmpty(candidate) && HostPortPattern().IsMatch(candidate);

    private static (string Host, int Port, string Database)? ParseHostPortDatabase(
        string hostPortDb)
    {
        var match = HostPortPattern().Match(hostPortDb);
        if (!match.Success)
        {
            return null;
        }

        var host = match.Groups["host"].Value;
        if (host.StartsWith(IPv6Open) && host.EndsWith(IPv6Close))
        {
            host = host[1..^1];
        }

        var port = DefaultPort;
        if (match.Groups["port"].Success)
        {
            if (!int.TryParse(
                    match.Groups["port"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out port)
                || port < 1
                || port > 65535)
            {
                return null;
            }
        }

        var database = string.Empty;
        if (match.Groups["rest"].Success)
        {
            var rest = match.Groups["rest"].Value.TrimStart(PathSeparator);
            var qIdx = rest.IndexOf(QuerySeparator);
            if (qIdx >= 0)
            {
                rest = rest[..qIdx];
            }

            database = DecodeComponent(rest);
        }

        return (host, port, database);
    }

    private static Dictionary<string, string> ParseQueryString(string? queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(queryString))
        {
            return result;
        }

        foreach (var pair in queryString.Split(
                     QueryPairSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = pair.IndexOf(QueryKeyValueSeparator);
            if (eqIdx > 0)
            {
                var key = Uri.UnescapeDataString(pair[..eqIdx]);
                var value = Uri.UnescapeDataString(pair[(eqIdx + 1)..]);
                result[key] = value;
            }
            else
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
            }
        }

        return result;
    }

    private static (string? Username, string? Password) ParseUserInfo(string? userInfoRaw)
    {
        if (string.IsNullOrEmpty(userInfoRaw))
        {
            return (null, null);
        }

        var colonIdx = userInfoRaw.IndexOf(PasswordSeparator);
        if (colonIdx >= 0)
        {
            return (DecodeComponent(userInfoRaw[..colonIdx]),
                       DecodeComponent(userInfoRaw[(colonIdx + 1)..]));
        }

        return (DecodeComponent(userInfoRaw), null);
    }

    /// <summary>
    /// Separates host/port/database from the query string,
    /// stripping any fragment leaked from an unencoded password.
    /// </summary>
    private static (string HostPortDb, string? QueryString) SplitHostAndQuery(string hostAndRest)
    {
        var queryIdx = hostAndRest.IndexOf(QuerySeparator);
        if (queryIdx >= 0)
        {
            var hostPortDb = hostAndRest[..queryIdx];
            var queryString = hostAndRest[(queryIdx + 1)..];

            var fragIdx = queryString.IndexOf(FragmentSeparator);
            if (fragIdx >= 0)
            {
                queryString = queryString[..fragIdx];
            }

            return (hostPortDb, queryString);
        }

        var fragInHost = hostAndRest.IndexOf(FragmentSeparator);
        return (fragInHost >= 0 ? hostAndRest[..fragInHost] : hostAndRest, null);
    }

    /// <summary>
    /// Finds the best <c>@</c> split between user-info and host by scoring every
    /// candidate whose right-hand side looks like a valid host. Rich host candidates
    /// beat bare ones, user-info that swallowed host- or query-like structure is
    /// penalized, and the rightmost valid split wins ties.
    /// </summary>
    private static (string? UserInfo, string HostAndRest) SplitUserInfoFromHost(string remainder)
    {
        (int Score, int Position)? best = null;

        var pos = remainder.LastIndexOf(UserInfoHostSeparator);
        while (pos >= 0)
        {
            var candidate = remainder[(pos + 1)..];

            // An unencoded '#' may have leaked past the real host — strip for validation only
            var fragIdx = candidate.IndexOf(FragmentSeparator);
            var candidateClean = fragIdx >= 0 ? candidate[..fragIdx] : candidate;

            if (IsValidHostStart(candidateClean))
            {
                var score = ScoreSplit(remainder[..pos], candidateClean);
                if (best is null || score > best.Value.Score)
                {
                    best = (score, pos);
                }
            }

            pos = pos > 0 ? remainder.LastIndexOf(UserInfoHostSeparator, pos - 1) : -1;
        }

        if (best is null)
        {
            return (null, remainder);
        }

        return (remainder[..best.Value.Position], remainder[(best.Value.Position + 1)..]);
    }

    /// <summary>
    /// Scores a candidate user-info/host split. A real host usually carries a dot,
    /// an explicit port or a database path, while a misparsed split tends to leave
    /// host- or query-like remnants inside the user-info.
    /// </summary>
    private static int ScoreSplit(string userInfo, string candidate)
    {
        var score = 0;

        var match = HostPortPattern().Match(candidate);
        if (match.Success)
        {
            if (match.Groups["host"].Value.Contains('.'))
            {
                score += 1;
            }

            if (match.Groups["port"].Success)
            {
                score += 1;
            }

            if (match.Groups["rest"].Success
                && match.Groups["rest"].Value.StartsWith(PathSeparator))
            {
                score += 1;
            }
        }

        if (userInfo.Contains(PathSeparator))
        {
            score -= 1;
        }

        if (userInfo.Contains(QuerySeparator))
        {
            score -= 1;
        }

        if (EmbeddedHostPortPattern().IsMatch(userInfo))
        {
            score -= 2;
        }

        return score;
    }

    private static (string Scheme, string Remainder)? StripScheme(string text)
    {
        if (text.StartsWith(PrefixPostgresql, StringComparison.OrdinalIgnoreCase))
        {
            return (SchemePostgresql, text[PrefixPostgresql.Length..]);
        }

        if (text.StartsWith(PrefixPostgres, StringComparison.OrdinalIgnoreCase))
        {
            return (SchemePostgres, text[PrefixPostgres.Length..]);
        }

        return null;
    }
}
