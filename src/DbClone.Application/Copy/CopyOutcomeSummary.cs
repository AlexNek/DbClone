using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbClone.Application.Copy;

/// <summary>
/// States a copy's verdict in one sentence, so the workflow keeps only its sequencing role.
/// A run that lost a function, a view, a materialized view or a trigger still completes; the
/// sentence says how many objects were not created instead of claiming the run failed.
/// </summary>
public static class CopyOutcomeSummary
{
    /// <summary>
    /// The stages that write one failed-object warning per object, so their warnings count
    /// objects. A warning of the same kind from any other stage names a different problem — a
    /// value it could not set, a constraint it refused — and is not counted here.
    /// </summary>
    private static readonly ECopyStage[] ObjectLevelStages =
        [ECopyStage.RetryFunctions, ECopyStage.CreateViews, ECopyStage.CreateTriggers];

    /// <summary>
    /// Describes the outcome of a copy run for the status line: the plain completion sentence,
    /// the same sentence with the number of objects that were not created, or the failure
    /// sentence when the run carries errors.
    /// </summary>
    public static string Describe(CopyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Success)
            return $"Copy failed: {result.Errors.Count} errors";

        var elapsed = $"{result.TotalDuration.TotalSeconds:F1}s";
        var notCreated = result.Warnings.Count(
            w => w.Kind == EStageMessageKind.Failed && ObjectLevelStages.Contains(w.StageName));

        if (notCreated == 0)
            return $"Copy complete in {elapsed}";

        var objects = notCreated == 1 ? "object" : "objects";
        return $"Copy complete in {elapsed} — {notCreated} {objects} not created (see the log)";
    }
}
