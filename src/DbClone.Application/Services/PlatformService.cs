using CanonicalModel.Platforms;
using DbAdapter.Definitions;
using DbClone.Application.Interfaces;

namespace DbClone.Application.Services;

/// <summary>
/// Forwards the platform questions to the adapter's platform role, supplying the
/// engine dialect from the adapter itself so the caller never names it.
/// </summary>
public sealed class PlatformService : IPlatformService
{
    private readonly IDatabaseAdapter _adapter;

    public PlatformService(IDatabaseAdapter adapter) =>
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    private string Engine => _adapter.Info.Dialect;

    /// <inheritdoc />
    public IReadOnlyList<PlatformEntry> GetAllPlatforms() => _adapter.Platform.GetAllPlatforms(Engine);

    /// <inheritdoc />
    public string? DetectPlatformId(string host) => _adapter.Platform.DetectPlatformId(Engine, host);

    /// <inheritdoc />
    public PlatformDefaults GetConnectionDefaults(string? platformId) =>
        _adapter.Platform.GetConnectionDefaults(Engine, platformId);
}
