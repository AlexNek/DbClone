namespace DbClone.Application.Platforms;

/// <summary>
/// Parses concrete server version strings and evaluates version range expressions
/// from .platform definition files.
/// Range expressions: "*", ">=15.0", ">=15.0 &lt;17.0", "&lt;17.0", "14.*", "14.x"
/// </summary>
public static class VersionRangeParser
{
    /// <summary>
    /// Parses a concrete server version string (e.g. "15.4", "16.1.2", "17.0")
    /// into a <see cref="Version"/>. Handles versions with extra suffixes
    /// (e.g. "15.4 (Ubuntu 15.4-1.pgdg22.04+1)") by taking the numeric prefix.
    /// </summary>
    public static Version ParseServerVersion(string serverVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverVersion);

        // Take only the numeric portion — server_version may contain suffixes
        var span = serverVersion.AsSpan();
        var end = 0;
        var dotCount = 0;

        for (var i = 0; i < span.Length; i++)
        {
            if (char.IsDigit(span[i]))
            {
                end = i + 1;
            }
            else if (span[i] == '.' && dotCount < 3)
            {
                dotCount++;
                end = i + 1;
            }
            else
            {
                break;
            }
        }

        // Trim trailing dot if present (e.g. "15.")
        var numericPart = serverVersion[..end].TrimEnd('.');

        // Version.Parse requires at least Major.Minor — append ".0" for major-only strings (e.g. "14")
        if (!numericPart.Contains('.'))
            numericPart += ".0";

        return Version.Parse(numericPart);
    }

    /// <summary>
    /// Returns true if the concrete <paramref name="version"/> satisfies the
    /// <paramref name="rangeExpression"/>.
    /// </summary>
    /// <param name="version">Concrete server version.</param>
    /// <param name="rangeExpression">
    /// One of: "*", ">=15.0", "&lt;17.0", ">=15.0 &lt;17.0", "14.*", "14.x" (space-separated constraints).
    /// </param>
    public static bool Satisfies(Version version, string rangeExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rangeExpression);

        var trimmed = rangeExpression.Trim();
        if (trimmed == "*")
            return true;

        // Split on whitespace — each token is a constraint like ">=15.0" or "<17.0"
        var constraints = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var constraint in constraints)
        {
            if (!SatisfiesSingle(version, constraint))
                return false;
        }

        return true;
    }

    private static bool SatisfiesSingle(Version version, string constraint)
    {
        if (constraint.StartsWith(">=", StringComparison.Ordinal))
        {
            var bound = Version.Parse(constraint[2..]);
            return Normalize(version) >= Normalize(bound);
        }

        if (constraint.StartsWith("<=", StringComparison.Ordinal))
        {
            var bound = Version.Parse(constraint[2..]);
            return Normalize(version) <= Normalize(bound);
        }

        if (constraint.StartsWith('>') && !constraint.StartsWith(">="))
        {
            var bound = Version.Parse(constraint[1..]);
            return Normalize(version) > Normalize(bound);
        }

        if (constraint.StartsWith('<') && !constraint.StartsWith("<="))
        {
            var bound = Version.Parse(constraint[1..]);
            return Normalize(version) < Normalize(bound);
        }

        // Wildcard minor: "14.*" or "14.x" → any 14.y
        if (constraint.EndsWith(".*", StringComparison.Ordinal)
            || constraint.EndsWith(".x", StringComparison.OrdinalIgnoreCase))
        {
            var majorPart = constraint[..^2]; // strip ".*" or ".x"
            if (int.TryParse(majorPart, out var major))
            {
                var v = Normalize(version);
                return v.Major == major;
            }
        }

        // Bare version = exact match
        var exact = Version.Parse(constraint);
        return Normalize(version) == Normalize(exact);
    }

    /// <summary>
    /// Normalizes a version to at least Major.Minor so comparisons work
    /// consistently (e.g. Version(15) vs Version(15, 0)).
    /// </summary>
    private static Version Normalize(Version v) =>
        v.Minor < 0 ? new Version(v.Major, 0) : v;

    /// <summary>
    /// Returns true if two range expressions can both be satisfied by some version.
    /// Used at load time to detect authoring mistakes in .platform files.
    /// Tests a set of probe versions covering PostgreSQL 1–30.
    /// </summary>
    public static bool RangesOverlap(string range1, string range2)
    {
        // Probe versions: major 1–30 at .0 and .5 — covers all realistic PostgreSQL versions
        for (var major = 1; major <= 30; major++)
        {
            foreach (var minor in new[] { 0, 5 })
            {
                var probe = new Version(major, minor);
                if (Satisfies(probe, range1) && Satisfies(probe, range2))
                    return true;
            }
        }

        return false;
    }
}
