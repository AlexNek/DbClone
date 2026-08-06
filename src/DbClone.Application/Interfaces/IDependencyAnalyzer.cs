using DbClone.Application.DTOs;
using DbClone.Application.Models;

namespace DbClone.Application.Interfaces;

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
