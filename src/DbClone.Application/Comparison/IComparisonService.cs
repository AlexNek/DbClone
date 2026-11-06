using DbClone.Application.DTOs;
using DbClone.Application.TableFilter;
using CanonicalModel.Enums;

namespace DbClone.Application.Comparison;

/// <summary>
/// Compares two databases and reports the differences.
/// Replaces the UI-layer <c>WorkflowState</c> with a plain <see cref="Action{T1,T2}"/>
/// so the service can live in the application layer without depending on UI types.
/// </summary>
public interface IComparisonService
{
    Task<CompareDatabasesResult> CompareDatabasesAsync(
        ConnectionInfo source,
        ConnectionInfo destination,
        Action<ELogLevel, string> log,
        EVerifyMode mode,
        bool excludePlatformSchemas,
        IProgress<CompareProgressInfo>? progress,
        Func<CancellationToken, Task>? waitWhilePaused,
        CancellationToken ct,
        TableSelectionSpec? tableSelection = null);
}
