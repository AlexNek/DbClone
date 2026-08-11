using DbClone.Application.DTOs;
using DbClone.Application.Enums;

namespace DbClone.UI.Services;

/// <summary>
/// Renders structured <see cref="StageDetail"/> facts into display strings.
/// Single source of truth for all pipeline message presentation in the UI.
/// Future localization replaces this class without touching infrastructure.
/// </summary>
internal static class StageDetailRenderer
{
    public static string Render(StageDetail d) => d.Kind switch
    {
        EStageMessageKind.Created => d.ObjectName ?? "",
        EStageMessageKind.Failed => $"FAIL: {d.ObjectName}{ReasonSuffix(d)}",
        EStageMessageKind.Skipped => RenderSkipped(d),
        EStageMessageKind.Deferred => $"DEFERRED: {d.ObjectName}{ReasonSuffix(d)}",
        EStageMessageKind.Excluded => $"EXCLUDED: {d.ObjectName}{ReasonSuffix(d)}",
        EStageMessageKind.Altered => $"{d.ObjectName}{ReasonSuffix(d)}",
        EStageMessageKind.Match => $"OK: {d.ObjectName}{DetailSuffix(d)}",
        EStageMessageKind.Mismatch => $"MISMATCH: {d.ObjectName}{DetailSuffix(d)}",
        EStageMessageKind.StillMismatched => RenderStillMismatched(d),
        EStageMessageKind.Fixed => $"OK: {d.ObjectName}: {d.Get<long>(PropKeys.DestRows)} rows (fixed)",
        EStageMessageKind.ConnectionFailed => RenderConnectionFailed(d),
        EStageMessageKind.Cancelled => "Cancelled",
        EStageMessageKind.Summary => RenderSummary(d),
        EStageMessageKind.Count => RenderCount(d),
        EStageMessageKind.Statistic => RenderStatistic(d),
        EStageMessageKind.VersionInfo => RenderVersionInfo(d),
        EStageMessageKind.Exception => d.Get<string>(PropKeys.Reason) ?? "Unknown error",
        EStageMessageKind.InfrastructureStatus => RenderInfrastructure(d),
        _ => d.ObjectName ?? d.Kind.ToString()
    };

    /// <summary>Renders a CopyError into a display string.</summary>
    public static string RenderError(CopyError error)
    {
        var reason = error.Properties is not null &&
                     error.Properties.TryGetValue(PropKeys.Reason, out var r)
            ? (string)r
            : null;

        return error.Kind switch
        {
            EStageMessageKind.Failed => error.ObjectName is not null
                ? $"{error.ObjectName}{(reason is not null ? $": {reason}" : "")}"
                : reason ?? "Stage failed",
            EStageMessageKind.Skipped => error.ObjectName is not null
                ? $"{error.ObjectName}{(reason is not null ? $": {reason}" : "")}"
                : reason ?? "Skipped",
            EStageMessageKind.Exception => reason ?? "Unexpected error",
            EStageMessageKind.ConnectionFailed => error.Properties is not null &&
                error.Properties.TryGetValue(PropKeys.Side, out var s)
                ? $"Cannot establish live connection to {((ECompareSide)s).ToDisplayText()}"
                : "Connection failed",
            _ => reason ?? error.ObjectName ?? error.Kind.ToString()
        };
    }

    /// <summary>Renders a CopyWarning into a display string.</summary>
    public static string RenderWarning(CopyWarning warning)
    {
        var reason = warning.Properties is not null &&
                     warning.Properties.TryGetValue(PropKeys.Reason, out var r)
            ? (string)r
            : null;

        return warning.Kind switch
        {
            EStageMessageKind.Failed => $"FAIL: {warning.ObjectName}{(reason is not null ? $": {reason}" : "")}",
            EStageMessageKind.Skipped => $"SKIP: {warning.ObjectName}{(reason is not null ? $": {reason}" : "")}",
            _ => reason ?? warning.ObjectName ?? warning.Kind.ToString()
        };
    }

    /// <summary>Renders a CopyWarning for the final summary section (includes stage context).</summary>
    public static string RenderWarningSummary(CopyWarning warning)
    {
        var reason = warning.Properties is not null &&
                     warning.Properties.TryGetValue(PropKeys.Reason, out var r)
            ? (string)r
            : null;

        var stage = warning.StageName.DisplayName();
        var prefix = warning.Kind switch
        {
            EStageMessageKind.Failed => "FAIL",
            EStageMessageKind.Skipped => "SKIP",
            _ => warning.Kind.ToString()
        };

        return $"{prefix} [{stage}]: {warning.ObjectName}{(reason is not null ? $": {reason}" : "")}";
    }

    // ─── Private renderers ───────────────────────────────────────────────

    private static string RenderSkipped(StageDetail d) =>
        d.ObjectName is null
            ? $"Skipped ({Reason(d)})"
            : $"SKIPPED: {d.ObjectName}{ReasonSuffix(d)}";

    private static string RenderStillMismatched(StageDetail d) =>
        $"STILL MISMATCHED: {d.ObjectName}: source={d.Get<long>(PropKeys.SourceRows)}, dest={d.Get<long>(PropKeys.DestRows)}";

    private static string RenderConnectionFailed(StageDetail d) =>
        $"Cannot establish live connection to {d.Get<ECompareSide>(PropKeys.Side).ToDisplayText()}";

    private static string RenderSummary(StageDetail d)
    {
        var matched = d.Get<int>(PropKeys.Matched);
        var failed = d.Get<int>(PropKeys.Failed);

        if (d.Has(PropKeys.Total))
        {
            var total = d.Get<int>(PropKeys.Total);

            // Four-count summary (from Summary(total, succeeded, failed, skipped))
            if (d.Has(PropKeys.Skipped))
            {
                var skipped = d.Get<int>(PropKeys.Skipped);
                if (skipped > 0 && failed > 0)
                    return $"{total} total: {matched} succeeded, {failed} failed, {skipped} skipped";
                if (skipped > 0)
                    return $"{total} total: {matched} succeeded, {skipped} skipped";
                if (failed > 0)
                    return $"{total} total: {matched} succeeded, {failed} failed";
                return $"All {matched} succeeded";
            }

            // Three-count retry summary (from Summary(total, succeeded, failed))
            return failed > 0
                ? $"Retried {total}: {matched} succeeded, {failed} failed"
                : $"All {matched} succeeded";
        }

        return failed > 0
            ? $"{matched} matched, {failed} mismatched"
            : $"All {matched} tables match";
    }

    private static string RenderCount(StageDetail d)
    {
        var objectType = d.Get<EDatabaseObjectType>(PropKeys.ObjectType);
        var count = d.Get<int>(PropKeys.Count);
        return $"{objectType}s: {count}";
    }

    private static string RenderStatistic(StageDetail d)
    {
        var label = d.Get<string>(PropKeys.Reason);
        var value = d.Properties?.TryGetValue(PropKeys.Count, out var v) == true ? v : "";
        return value is long l ? $"{label}: {l:N0}" : $"{label}: {value}";
    }

    private static string RenderVersionInfo(StageDetail d)
    {
        var side = d.Get<ECompareSide>(PropKeys.Side);
        var version = d.Get<string>(PropKeys.Version);
        return $"{side.ToDisplayText()}: {version}";
    }

    private static string RenderInfrastructure(StageDetail d)
    {
        var reason = Reason(d);
        if (d.Has(PropKeys.Expected))
            return $"{reason}: expected {d.Get<int>(PropKeys.Expected)}, found {d.Get<int>(PropKeys.Actual)}";
        return reason;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static string Reason(StageDetail d) =>
        d.Properties?.TryGetValue(PropKeys.Reason, out var v) == true ? (string)v : "";

    private static string ReasonSuffix(StageDetail d) =>
        d.Properties?.TryGetValue(PropKeys.Reason, out var v) == true ? $": {v}" : "";

    private static string DetailSuffix(StageDetail d) =>
        d.Properties?.TryGetValue(PropKeys.Detail, out var v) == true ? (string)v : "";
}
