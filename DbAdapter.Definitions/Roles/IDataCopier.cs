using CanonicalModel.Models;
using CanonicalModel.DTOs;

namespace DbAdapter.Definitions;

/// <summary>
/// Copies data between databases.
/// </summary>
public interface IDataCopier
{
    /// <summary>
    /// Copies all data from source tables to destination tables.
    /// </summary>
    Task<CopyStatistics> CopyDataAsync(
        IReadOnlyList<TableDefinition> tables,
        CopyOptions options,
        IProgress<TableCopyProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
