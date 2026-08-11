using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.Application.TableFilter;

using Microsoft.Extensions.Logging;

namespace DbClone.Application.Copy;

/// <summary>
/// Resolves the user's table selection spec against the freshly read source model.
/// Provider-independent: it only transforms <see cref="CopyContext.SourceModel"/>,
/// so downstream stages operate on the already-filtered model without any changes.
/// Marked critical — a failed filter resolution aborts the pipeline instead of
/// silently continuing with the full scope.
/// </summary>
public sealed class ApplyTableFilterStage : ICopyStage
{
    private readonly ITableFilterApplier _filterApplier;

    private readonly ILogger<ApplyTableFilterStage> _logger;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.ApplyTableFilter;

    /// <inheritdoc />
    public int Order => 35;

    /// <summary>Initializes a new instance.</summary>
    public ApplyTableFilterStage(
        ILogger<ApplyTableFilterStage> logger,
        ITableFilterApplier filterApplier)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _filterApplier = filterApplier ?? throw new ArgumentNullException(nameof(filterApplier));
    }

    /// <inheritdoc />
    public Task<StageResult> ExecuteAsync(
        CopyContext context,
        CancellationToken cancellationToken = default)
    {
        var spec = context.Request.Options.TableSelection;

        if (spec is not { IsActive: true })
        {
            return Task.FromResult(
                new StageResult(
                    Name,
                    true,
                    TimeSpan.Zero,
                    0,
                    [StageDetail.Infrastructure("No table selection active", ELogLevel.Info)]));
        }

        var model = context.SourceModel
            ?? throw new InvalidOperationException("Source model is not available.");

        var totalTables = model.Tables.Count;
        var result = _filterApplier.Apply(model, spec);

        EmitWarnings(context, result.Report);

        // A database that genuinely has no tables is a no-op, not an error.
        // But filtering a non-empty database down to zero tables indicates a
        // stale or broken selection — fail fast instead of copying nothing.
        if (totalTables > 0 && result.FilteredModel.Tables.Count == 0)
        {
            _logger.LogError(
                "Table selection excluded all {TableCount} tables",
                totalTables);

            context.Errors.Add(
                new CopyError(
                    StageName: Name,
                    Kind: EStageMessageKind.Failed,
                    ObjectName: null,
                    Properties: new Dictionary<string, object>
                    {
                        [PropKeys.Reason] =
                            "The table selection excludes every table in the source database"
                    },
                    Exception: null));

            return Task.FromResult(
                new StageResult(
                    Name,
                    false,
                    TimeSpan.Zero,
                    0,
                    [StageDetail.Failed(
                        "Table selection",
                        "Selection excludes every table in the source database")]));
        }

        context.SourceModel = result.FilteredModel;

        _logger.LogInformation(
            "Table selection applied: {Removed} tables excluded, {Remaining} remain",
            result.Report.RemovedTables.Count,
            result.FilteredModel.Tables.Count);

        return Task.FromResult(
            new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                result.Report.RemovedTables.Count,
                [
                    StageDetail.Statistic("Tables excluded", result.Report.RemovedTables.Count),
                    StageDetail.Statistic("Tables remaining", result.FilteredModel.Tables.Count)
                ]));
    }

    private static void EmitWarnings(CopyContext context, TableFilterReport report)
    {
        foreach (var stale in report.StaleExclusions)
        {
            context.Warnings.Add(
                Warning(stale.FullName, "No longer exists in the source database"));
        }

        foreach (var fk in report.DroppedForeignKeys)
        {
            context.Warnings.Add(
                Warning(
                    $"{fk.OwningTable.FullName}.{fk.ConstraintName}",
                    $"Foreign key references excluded table {fk.ReferencedTable.FullName}"));
        }

        foreach (var view in report.SkippedViews)
        {
            context.Warnings.Add(
                Warning(view.FullName, "Depends on an excluded table"));
        }

        foreach (var partition in report.OrphanedPartitions)
        {
            context.Warnings.Add(
                Warning(partition.FullName, "Parent table is excluded"));
        }
    }

    private static CopyWarning Warning(string objectName, string reason) =>
        new(
            ECopyStage.ApplyTableFilter,
            EStageMessageKind.Skipped,
            objectName,
            new Dictionary<string, object> { [PropKeys.Reason] = reason });
}
