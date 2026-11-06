using CanonicalModel.Platforms;
using DbClone.Application.Interfaces;

namespace UI.Tests.Fakes;

/// <summary>
/// Provides a fake <see cref="IPlatformService"/> for tests: an empty platform list,
/// the plain-engine defaults, and <c>null</c> host detection (generic PostgreSQL).
/// </summary>
public static class TestPlatformResolver
{
    public static IPlatformService Create() => new FakePlatformService();

    private sealed class FakePlatformService : IPlatformService
    {
        public IReadOnlyList<PlatformEntry> GetAllPlatforms() => [];

        public string? DetectPlatformId(string host) => null;

        public PlatformDefaults GetConnectionDefaults(string? platformId) =>
            new(Port: 5432, SslMode: "Prefer");
    }
}
