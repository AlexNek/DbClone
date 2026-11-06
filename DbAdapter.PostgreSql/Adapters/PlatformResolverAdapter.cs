using DbAdapter.Definitions;
using CanonicalModel.Platforms;
using DbAdapter.PostgreSql.Platforms;

namespace DbAdapter.PostgreSql.Adapters;

/// <summary>
/// The platform role, forwarding to the resolver the application still owns.
/// </summary>
internal sealed class PlatformResolverAdapter : IPlatformResolver
{
    private readonly PlatformSchemaResolver _resolver;

    public PlatformResolverAdapter(PlatformSchemaResolver resolver) =>
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    /// <inheritdoc />
    public PlatformResolution Resolve(string engine, string host, string serverVersion) =>
        _resolver.Resolve(engine, host, serverVersion);

    /// <inheritdoc />
    public string? DetectPlatformId(string engine, string host) =>
        _resolver.DetectPlatformId(engine, host);

    /// <inheritdoc />
    public PlatformDefaults GetConnectionDefaults(string engine, string? platformId) =>
        _resolver.GetConnectionDefaults(engine, platformId);

    /// <inheritdoc />
    public IReadOnlyList<PlatformEntry> GetAllPlatforms(string engine) =>
        _resolver.GetAllPlatforms(engine);
}
