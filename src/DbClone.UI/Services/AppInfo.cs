using System.Reflection;

namespace DbClone.UI.Services;

/// <summary>
/// Centralized application metadata (name, version) derived from GitVersion assembly info.
/// </summary>
public static class AppInfo
{
    private const string DefaultProductName = "DbClone";

    private const string FieldCommitsSinceVersionSource = "CommitsSinceVersionSource";

    private const string FieldMajorMinorPatch = "MajorMinorPatch";

    private const string GitVersionInfoTypeName = "GitVersionInformation";

    private const string UnknownVersion = "0.0.0-dev";

    public static string ProductName { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyProductAttribute>()?.Product
        is { Length: > 0 } product
            ? product
            : DefaultProductName;

    public static string RepositoryUrl { get; } = "https://github.com/AlexNek/DbClone";

    /// <summary>Short version string, e.g. "1.2.3" or "1.2.3 (build 44)".</summary>
    public static string Version { get; } = BuildVersion();

    /// <summary>Full title, e.g. "DbClone — PostgreSQL Database Copy Tool v1.2.3 (build 44)".</summary>
    public static string FullTitle { get; } =
        $"{ProductName} — PostgreSQL Database Copy Tool v{Version}";

    private static string BuildVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var gvType = assembly.GetType(GitVersionInfoTypeName);
        var majorMinorPatch = GetField(gvType, FieldMajorMinorPatch) ?? UnknownVersion;
        var commitsSinceTag = GetField(gvType, FieldCommitsSinceVersionSource);

        // increment: None on master means MajorMinorPatch equals the last tag;
        // commits since tag are shown as build number.
        return int.TryParse(commitsSinceTag, out var commits) && commits > 0
                   ? $"{majorMinorPatch} (build {commits})"
                   : majorMinorPatch;
    }

    private static string? GetField(Type? type, string fieldName) =>
        type?.GetField(fieldName)?.GetValue(null) as string;
}
