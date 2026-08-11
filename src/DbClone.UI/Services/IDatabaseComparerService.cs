using DbClone.Application.Enums;
using DbClone.Application.TableFilter;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

namespace DbClone.UI.Services;

public interface IDatabaseComparerService
{
    Task<CompareDatabasesResult> CompareDatabasesAsync(
        ConnectionViewModel source,
        ConnectionViewModel destination,
        WorkflowState state,
        EVerifyMode mode,
        bool excludePlatformSchemas,
        IProgress<CompareProgressInfo>? progress,
        Func<CancellationToken, Task>? waitWhilePaused,
        CancellationToken ct,
        TableSelectionSpec? tableSelection = null);
}

public sealed record CompareDatabasesResult(
    List<CompareResultItem> Items,
    int TotalIdentical,
    int TotalNotices,
    int TotalDifferent,
    int TotalMissingSource,
    int TotalMissingDest,
    int TotalSkipped,
    int TotalErrors);
