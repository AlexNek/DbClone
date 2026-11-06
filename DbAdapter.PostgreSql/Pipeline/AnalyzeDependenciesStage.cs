using CanonicalModel.Enums;
using CanonicalModel.DTOs;
using DbAdapter.Definitions;

namespace DbAdapter.PostgreSql.Pipeline;

/// <summary>
/// Analyzes dependencies between database objects.
/// </summary>
public sealed class AnalyzeDependenciesStage : ICopyStage
{
    private readonly DependencyAnalysis.PgDependencyAnalyzer _analyzer;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.AnalyzeDependencies;

    /// <inheritdoc />
    public int Order => 40;

    /// <summary>Initializes a new instance.</summary>
    public AnalyzeDependenciesStage(DependencyAnalysis.PgDependencyAnalyzer analyzer) =>
        _analyzer = analyzer;

    /// <inheritdoc />
    public async Task<StageResult> ExecuteAsync(
        CopyContext context,
        CancellationToken cancellationToken = default)
    {
        context.DependencyResult =
            await _analyzer.AnalyzeAsync(context.SourceModel!, cancellationToken);

        return new StageResult(
            Name,
            true,
            TimeSpan.Zero,
            context.DependencyResult.OrderedObjects.Count,
                [
                    StageDetail.Statistic("Ordered", context.DependencyResult.OrderedObjects.Count),
                    StageDetail.Statistic("Cycles", context.DependencyResult.CircularDependencies.Count)
                ]);
    }
}
