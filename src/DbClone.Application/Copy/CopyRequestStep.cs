using CanonicalModel.DTOs;
using DbAdapter.Definitions;

namespace DbClone.Application.Copy;

/// <summary>
/// What the table-selection step hands the caller: the request to run, the stage row for
/// the copy log, the warnings the filter raised, and the failed result instead of a
/// request when the selection excludes every table.
/// </summary>
/// <param name="Request">Request for the copy, or null when nothing must be copied.</param>
/// <param name="Stage">The table-filter row of the copy log.</param>
/// <param name="Warnings">Warnings raised while the selection was resolved.</param>
/// <param name="FailedResult">Failed copy result for the zero-table case, else null.</param>
public sealed record CopyRequestStep(
    CopyRequest? Request,
    StageResult Stage,
    IReadOnlyList<CopyWarning> Warnings,
    CopyResult? FailedResult);
