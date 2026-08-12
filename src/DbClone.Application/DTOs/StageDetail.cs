using DbClone.Application.Enums;

namespace DbClone.Application.DTOs;

/// <summary>
/// A single structured detail reported by a pipeline stage.
/// Carries semantic facts (kind, object name, properties) — never pre-rendered display text.
/// The UI layer composes human-readable strings via a renderer.
/// </summary>
/// <param name="Level">Severity assigned by the producing stage.</param>
/// <param name="Kind">Semantic category of the message.</param>
/// <param name="ObjectName">Database object this message refers to (table, view, function, etc.).</param>
/// <param name="Properties">Additional structured data (counts, reasons, sides).</param>
public sealed record StageDetail(
    ELogLevel Level,
    EStageMessageKind Kind,
    string? ObjectName = null,
    IReadOnlyDictionary<string, object>? Properties = null)
{
    // ─── Object lifecycle ────────────────────────────────────────────────

    /// <summary>Object was created successfully.</summary>
    public static StageDetail Created(string objectName) =>
        new(ELogLevel.Info, EStageMessageKind.Created, objectName);

    /// <summary>Object failed to create/copy.</summary>
    public static StageDetail Failed(string objectName, string? reason = null) =>
        new(ELogLevel.Error, EStageMessageKind.Failed, objectName, ReasonProps(reason));

    /// <summary>Object failed but stage overall succeeds (partial failure).</summary>
    public static StageDetail FailedWarning(string objectName, string? reason = null) =>
        new(ELogLevel.Warning, EStageMessageKind.Failed, objectName, ReasonProps(reason));

    /// <summary>Object was skipped.</summary>
    public static StageDetail Skipped(string? objectName = null, string? reason = null) =>
        new(ELogLevel.Info, EStageMessageKind.Skipped, objectName, ReasonProps(reason));

    /// <summary>Object was skipped with warning severity.</summary>
    public static StageDetail SkippedWarning(string objectName, string? reason = null) =>
        new(ELogLevel.Warning, EStageMessageKind.Skipped, objectName, ReasonProps(reason));

    /// <summary>Object was skipped due to a prior error (treated as error for reporting).</summary>
    public static StageDetail SkippedError(string objectName, string? reason = null) =>
        new(ELogLevel.Error, EStageMessageKind.Skipped, objectName, ReasonProps(reason));

    /// <summary>Object creation deferred to a later stage.</summary>
    public static StageDetail Deferred(string objectName, string? reason = null) =>
        new(ELogLevel.Warning, EStageMessageKind.Deferred, objectName, ReasonProps(reason));

    /// <summary>Object was excluded (e.g. no privilege).</summary>
    public static StageDetail Excluded(string objectName, string? reason = null) =>
        new(ELogLevel.Warning, EStageMessageKind.Excluded, objectName, ReasonProps(reason));

    /// <summary>Object was altered (e.g. column nullability reconciled).</summary>
    public static StageDetail Altered(string objectName, string? detail = null) =>
        new(ELogLevel.Info, EStageMessageKind.Altered, objectName, ReasonProps(detail));

    // ─── Validation ──────────────────────────────────────────────────────

    /// <summary>Object validated successfully.</summary>
    public static StageDetail Matched(string objectName, string? detail = null) =>
        new(ELogLevel.Info, EStageMessageKind.Match, objectName, DetailProps(detail));

    /// <summary>Object validation found a mismatch.</summary>
    public static StageDetail Mismatched(string objectName, string? detail = null) =>
        new(ELogLevel.Error, EStageMessageKind.Mismatch, objectName, DetailProps(detail));

    /// <summary>Object still mismatched after re-copy.</summary>
    public static StageDetail StillMismatched(string objectName, long sourceRows, long destRows) =>
        new(ELogLevel.Error, EStageMessageKind.StillMismatched, objectName,
            new Dictionary<string, object>
            {
                [PropKeys.SourceRows] = sourceRows,
                [PropKeys.DestRows] = destRows
            });

    /// <summary>Object fixed after re-copy.</summary>
    public static StageDetail Fixed(string objectName, long destRows) =>
        new(ELogLevel.Info, EStageMessageKind.Fixed, objectName,
            new Dictionary<string, object> { [PropKeys.DestRows] = destRows });

    // ─── Connection ──────────────────────────────────────────────────────

    /// <summary>Connection to the specified side could not be established.</summary>
    public static StageDetail ConnectionFailed(ECompareSide side, string? reason = null, string? host = null)
    {
        var props = new Dictionary<string, object> { [PropKeys.Side] = side };
        if (reason is not null)
            props[PropKeys.Reason] = reason;
        if (host is not null)
            props[PropKeys.Host] = host;
        return new(ELogLevel.Error, EStageMessageKind.ConnectionFailed, Properties: props);
    }

    // ─── Stage-level ─────────────────────────────────────────────────────

    /// <summary>Stage was cancelled.</summary>
    public static StageDetail Cancelled() =>
        new(ELogLevel.Info, EStageMessageKind.Cancelled);

    /// <summary>Aggregate summary (matched vs failed counts).</summary>
    public static StageDetail Summary(int matched, int failed) =>
        new(failed > 0 ? ELogLevel.Warning : ELogLevel.Info,
            EStageMessageKind.Summary,
            Properties: new Dictionary<string, object>
            {
                [PropKeys.Matched] = matched,
                [PropKeys.Failed] = failed
            });

    /// <summary>Aggregate summary with total (e.g. retried functions).</summary>
    public static StageDetail Summary(int total, int succeeded, int failed) =>
        new(failed > 0 ? ELogLevel.Warning : ELogLevel.Info,
            EStageMessageKind.Summary,
            Properties: new Dictionary<string, object>
            {
                [PropKeys.Total] = total,
                [PropKeys.Matched] = succeeded,
                [PropKeys.Failed] = failed
            });

    /// <summary>Aggregate summary with total, succeeded, failed, and skipped counts.</summary>
    public static StageDetail Summary(int total, int succeeded, int failed, int skipped) =>
        new(failed > 0 || skipped > 0 ? ELogLevel.Warning : ELogLevel.Info,
            EStageMessageKind.Summary,
            Properties: new Dictionary<string, object>
            {
                [PropKeys.Total] = total,
                [PropKeys.Matched] = succeeded,
                [PropKeys.Failed] = failed,
                [PropKeys.Skipped] = skipped
            });

    /// <summary>Object count for a database object type (used by ObjectsPanel).</summary>
    public static StageDetail Count(EDatabaseObjectType objectType, int count) =>
        new(ELogLevel.Info, EStageMessageKind.Count,
            Properties: new Dictionary<string, object>
            {
                [PropKeys.ObjectType] = objectType,
                [PropKeys.Count] = count
            });

    /// <summary>Numeric statistic (rows, bytes, etc.).</summary>
    public static StageDetail Statistic(string label, object value) =>
        new(ELogLevel.Info, EStageMessageKind.Statistic,
            Properties: new Dictionary<string, object>
            {
                [PropKeys.Reason] = label,
                [PropKeys.Count] = value
            });

    /// <summary>Version information for a connection side.</summary>
    public static StageDetail VersionInfo(ECompareSide side, string version) =>
        new(ELogLevel.Info, EStageMessageKind.VersionInfo,
            Properties: new Dictionary<string, object>
            {
                [PropKeys.Side] = side,
                [PropKeys.Version] = version
            });

    /// <summary>Exception message passthrough (raw data, not composed).</summary>
    public static StageDetail Exception(string message) =>
        new(ELogLevel.Error, EStageMessageKind.Exception,
            Properties: new Dictionary<string, object> { [PropKeys.Reason] = message });

    /// <summary>Exception with warning severity (non-fatal).</summary>
    public static StageDetail ExceptionWarning(string message) =>
        new(ELogLevel.Warning, EStageMessageKind.Exception,
            Properties: new Dictionary<string, object> { [PropKeys.Reason] = message });

    // ─── Infrastructure ──────────────────────────────────────────────────

    /// <summary>Infrastructure status message (information_schema, privileges, etc.).</summary>
    public static StageDetail Infrastructure(string reason, ELogLevel level = ELogLevel.Warning) =>
        new(level, EStageMessageKind.InfrastructureStatus,
            Properties: new Dictionary<string, object> { [PropKeys.Reason] = reason });

    /// <summary>Count mismatch warning (expected vs actual).</summary>
    public static StageDetail CountMismatch(string label, int expected, int actual, ELogLevel level = ELogLevel.Warning) =>
        new(level, EStageMessageKind.InfrastructureStatus,
            Properties: new Dictionary<string, object>
            {
                [PropKeys.Reason] = label,
                [PropKeys.Expected] = expected,
                [PropKeys.Actual] = actual
            });

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>Typed accessor for Properties values.</summary>
    public T Get<T>(string key) =>
        Properties is not null && Properties.TryGetValue(key, out var v)
            ? (T)v
            : default!;

    /// <summary>Checks whether a property key exists.</summary>
    public bool Has(string key) => Properties is not null && Properties.ContainsKey(key);

    private static IReadOnlyDictionary<string, object>? ReasonProps(string? reason) =>
        reason is null ? null : new Dictionary<string, object> { [PropKeys.Reason] = reason };

    private static IReadOnlyDictionary<string, object>? DetailProps(string? detail) =>
        detail is null ? null : new Dictionary<string, object> { [PropKeys.Detail] = detail };
}
