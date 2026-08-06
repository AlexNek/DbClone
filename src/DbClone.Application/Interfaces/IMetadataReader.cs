using DbClone.Application.Models;
using DbClone.Application.Platforms;

namespace DbClone.Application.Interfaces;

/// <summary>
/// Reads metadata from a database.
/// </summary>
public interface IMetadataReader
{
    /// <summary>
    /// Reads the complete database model.
    /// When <paramref name="excludePlatformSchemas"/> is true, schemas owned by
    /// platform service roles (non-login, non-current-user) are excluded.
    /// Default is false — callers opt in to filtering (e.g. comparison).
    /// </summary>
    /// <param name="excludePlatformSchemas">Whether to exclude platform-managed schemas.</param>
    /// <param name="platformResolution">
    /// Resolved platform definition (system/platform schemas). When null, falls back
    /// to hardcoded system schema list.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DatabaseModel> ReadDatabaseModelAsync(
        bool excludePlatformSchemas = false,
        PlatformResolution? platformResolution = null,
        CancellationToken cancellationToken = default);
}
