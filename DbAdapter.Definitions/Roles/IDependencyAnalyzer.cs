using CanonicalModel.Models;
using CanonicalModel.DTOs;

namespace DbAdapter.Definitions;

/// <summary>
/// Analyzes dependencies between database objects.
/// </summary>
public interface IDependencyAnalyzer
{
    /// <summary>
    /// Analyzes dependencies and returns objects in topological order.
    /// </summary>
    Task<DependencyResult> AnalyzeAsync(
        DatabaseModel model,
        CancellationToken cancellationToken = default);
}
