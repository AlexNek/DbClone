using DbClone.Application.Copy;

using FluentAssertions;

using CanonicalModel.Enums;
using CanonicalModel.DTOs;

namespace Application.Tests;

/// <summary>
/// Tests for <see cref="CopyOutcomeSummary"/>: the sentence a run ends with, with no database
/// and no UI. Only the stages that write one warning per object contribute to the count.
/// </summary>
public class CopyOutcomeSummaryTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(241.1);

    private static readonly string Elapsed = $"{Duration.TotalSeconds:F1}s";

    [Fact]
    public void Describe_CompleteRunWithNoWarnings_StatesCompletionOnly()
    {
        var result = MakeResult(warnings: []);

        var sentence = CopyOutcomeSummary.Describe(result);

        sentence.Should().Be($"Copy complete in {Elapsed}");
    }

    [Fact]
    public void Describe_OneObjectNotCreated_CountsIt()
    {
        var result = MakeResult(
            warnings:
            [
                Failed(ECopyStage.RetryFunctions, "realtime.list_changes")
            ]);

        var sentence = CopyOutcomeSummary.Describe(result);

        sentence.Should().Be($"Copy complete in {Elapsed} — 1 object not created (see the log)");
    }

    [Fact]
    public void Describe_SeveralObjectsNotCreated_CountsEachOfThem()
    {
        var result = MakeResult(
            warnings:
            [
                Failed(ECopyStage.RetryFunctions, "realtime.list_changes"),
                Failed(ECopyStage.CreateViews, "public.monthly_report"),
                Failed(ECopyStage.CreateViews, "public.sales_totals"),
                Failed(ECopyStage.CreateTriggers, "public.trg_audit")
            ]);

        var sentence = CopyOutcomeSummary.Describe(result);

        sentence.Should().Be($"Copy complete in {Elapsed} — 4 objects not created (see the log)");
    }

    [Fact]
    public void Describe_SkippedWarningsOnly_StatesCompletionOnly()
    {
        var result = MakeResult(
            warnings:
            [
                new CopyWarning(ECopyStage.RetryFunctions, EStageMessageKind.Skipped, "public.deferred", null),
                new CopyWarning(ECopyStage.CreateConstraints, EStageMessageKind.Skipped, "public.orders", null)
            ]);

        var sentence = CopyOutcomeSummary.Describe(result);

        sentence.Should().Be($"Copy complete in {Elapsed}");
    }

    [Fact]
    public void Describe_FailedWarningFromAnotherStage_IsNotCounted()
    {
        var result = MakeResult(
            warnings:
            [
                Failed(ECopyStage.SyncSequences, "public.broken_seq"),
                Failed(ECopyStage.ReconcileColumns, "public.orders"),
                Failed(ECopyStage.CreateConstraints, "public.orders"),
                Failed(ECopyStage.CreateExtensions, "public.some_func")
            ]);

        var sentence = CopyOutcomeSummary.Describe(result);

        sentence.Should().Be($"Copy complete in {Elapsed}");
    }

    [Fact]
    public void Describe_RunWithErrors_StatesFailure()
    {
        var result = MakeResult(
            success: false,
            errors:
            [
                new CopyError(ECopyStage.CopyData, EStageMessageKind.Failed, "public.orders", null, null),
                new CopyError(ECopyStage.CreateTables, EStageMessageKind.Failed, "public.items", null, null)
            ]);

        var sentence = CopyOutcomeSummary.Describe(result);

        sentence.Should().Be("Copy failed: 2 errors");
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static CopyWarning Failed(ECopyStage stage, string objectName) =>
        new(stage, EStageMessageKind.Failed, objectName, null);

    private static CopyResult MakeResult(
        bool success = true,
        IReadOnlyList<CopyWarning>? warnings = null,
        IReadOnlyList<CopyError>? errors = null) =>
        new(
            success,
            Duration,
            [],
            warnings ?? [],
            errors ?? [],
            new CopyStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0));
}
