using DbClone.Application.Platforms;

using Microsoft.Extensions.Logging.Abstractions;

namespace UI.Tests.Fakes;

/// <summary>
/// Provides a <see cref="PlatformSchemaResolver"/> for tests.
/// Points to a non-existent directory so no platform files are loaded
/// and DetectPlatformId always returns null (generic PostgreSQL).
/// </summary>
public static class TestPlatformResolver
{
    public static PlatformSchemaResolver Create()
    {
        var loader = new PlatformDefinitionLoader(
            Path.Combine(Path.GetTempPath(), "__nonexistent_platforms__"),
            NullLogger<PlatformDefinitionLoader>.Instance);
        return new PlatformSchemaResolver(loader, NullLogger<PlatformSchemaResolver>.Instance);
    }
}
