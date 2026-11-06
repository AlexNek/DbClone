using CanonicalModel.Platforms;
using DbAdapter.Definitions;
using DbClone.Application.Adapters;
using DbClone.Application.Interfaces;

namespace DbClone.Application.Services;

/// <summary>
/// Forwards the platform questions to the adapters' platform roles. The list is the union of
/// every installed adapter's platforms; a specific platform id is resolved through the adapter
/// that owns it, so nothing here names an engine (refactors/09 P4). With one engine installed the
/// answers are exactly those the single adapter gave before.
/// </summary>
public sealed class PlatformService : IPlatformService
{
    private readonly IAdapterCatalog _catalog;

    public PlatformService(IAdapterCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <inheritdoc />
    public IReadOnlyList<PlatformEntry> GetAllPlatforms() => _catalog.Platforms;

    /// <inheritdoc />
    public string? DetectPlatformId(string host)
    {
        foreach (var adapter in _catalog.All)
        {
            var id = adapter.Platform.DetectPlatformId(adapter.Info.Dialect, host);
            if (!string.IsNullOrWhiteSpace(id))
                return id;
        }

        return null;
    }

    /// <inheritdoc />
    public PlatformDefaults GetConnectionDefaults(string? platformId)
    {
        // A platform id belongs to exactly one adapter (the catalog refuses a duplicate); ask it.
        if (!string.IsNullOrWhiteSpace(platformId))
        {
            var owner = _catalog.All.FirstOrDefault(adapter =>
                adapter.Platform.GetAllPlatforms(adapter.Info.Dialect)
                    .Any(entry => string.Equals(entry.Id, platformId, StringComparison.OrdinalIgnoreCase)));
            if (owner is not null)
                return owner.Platform.GetConnectionDefaults(owner.Info.Dialect, platformId);
        }

        // A blank id, or one no adapter claims, falls back to the base engine of the installed adapter.
        var baseAdapter = _catalog.Resolve(null);
        return baseAdapter.Platform.GetConnectionDefaults(baseAdapter.Info.Dialect, platformId);
    }
}
