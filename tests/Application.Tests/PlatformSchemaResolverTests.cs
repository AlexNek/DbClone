using DbClone.Application.Platforms;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

namespace Application.Tests;

public sealed class PlatformSchemaResolverTests : IDisposable
{
    private readonly string _tempDir;

    public PlatformSchemaResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"platform_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private PlatformSchemaResolver CreateResolver()
    {
        var loader = new PlatformDefinitionLoader(_tempDir, NullLogger<PlatformDefinitionLoader>.Instance);
        return new PlatformSchemaResolver(loader, NullLogger<PlatformSchemaResolver>.Instance);
    }

    private void WritePlatformFile(string fileName, string json)
    {
        File.WriteAllText(Path.Combine(_tempDir, fileName), json);
    }

    [Fact]
    public void Resolve_NoFiles_ReturnsNone()
    {
        var resolver = CreateResolver();

        var result = resolver.Resolve("postgresql", "test.example.com", "15.4");

        result.DetectedPlatform.Should().BeNull();
        result.SystemSchemas.Should().BeEmpty();
        result.VersionWarning.Should().BeNull();
    }

    [Fact]
    public void Resolve_BaseEngineFile_ReturnsSystemSchemas()
    {
        WritePlatformFile("postgresql.platform", """
        {
          "engine": "postgresql",
          "displayName": "PostgreSQL",
          "formatVersion": "1.0",
          "versions": [{ "versionRange": "*", "systemSchemas": ["pg_catalog", "information_schema", "pg_toast"], "platformSchemas": [], "platformExtensions": [] }]
        }
        """);

        var resolver = CreateResolver();
        var result = resolver.Resolve("postgresql", "test.example.com", "15.4");

        result.DetectedPlatform.Should().BeNull();
        result.SystemSchemas.Should().Contain("pg_catalog");
        result.SystemSchemas.Should().Contain("information_schema");
        result.SystemSchemas.Should().Contain("pg_toast");
        result.PlatformSchemas.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_SupabaseHost_DetectsPlatform()
    {
        WritePlatformFile("postgresql.platform", """
        {
          "engine": "postgresql",
          "displayName": "PostgreSQL",
          "formatVersion": "1.0",
          "versions": [{ "versionRange": "*", "systemSchemas": ["pg_catalog", "information_schema", "pg_toast"], "platformSchemas": [], "platformExtensions": [] }]
        }
        """);
        WritePlatformFile("supabase.platform", """
        {
          "engine": "postgresql",
          "displayName": "Supabase",
          "formatVersion": "1.0",
          "detection": { "hostPatterns": ["*.supabase.co"] },
          "versions": [{ "versionRange": ">=15.0 <16.0", "systemSchemas": ["pg_catalog", "information_schema", "pg_toast"], "platformSchemas": ["auth", "storage", "extensions"], "platformExtensions": ["pgsodium"] }]
        }
        """);

        var resolver = CreateResolver();
        var result = resolver.Resolve("postgresql", "myproject.supabase.co", "15.4");

        result.DetectedPlatform.Should().Be("Supabase");
        result.PlatformSchemas.Should().Contain("auth");
        result.PlatformSchemas.Should().Contain("storage");
        result.PlatformSchemas.Should().Contain("extensions");
        result.PlatformExtensions.Should().Contain("pgsodium");
        result.VersionWarning.Should().BeNull();
    }

    [Fact]
    public void Resolve_UnknownHost_FallsBackToBaseEngine()
    {
        WritePlatformFile("postgresql.platform", """
        {
          "engine": "postgresql",
          "displayName": "PostgreSQL",
          "formatVersion": "1.0",
          "versions": [{ "versionRange": "*", "systemSchemas": ["pg_catalog", "information_schema", "pg_toast"], "platformSchemas": [], "platformExtensions": [] }]
        }
        """);
        WritePlatformFile("supabase.platform", """
        {
          "engine": "postgresql",
          "displayName": "Supabase",
          "formatVersion": "1.0",
          "detection": { "hostPatterns": ["*.supabase.co"] },
          "versions": [{ "versionRange": "*", "systemSchemas": ["pg_catalog"], "platformSchemas": ["auth"], "platformExtensions": [] }]
        }
        """);

        var resolver = CreateResolver();
        var result = resolver.Resolve("postgresql", "myserver.example.com", "15.4");

        result.DetectedPlatform.Should().BeNull();
        result.PlatformSchemas.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_UnsupportedVersion_ReturnsWarningAndBaseSystemSchemas()
    {
        WritePlatformFile("postgresql.platform", """
        {
          "engine": "postgresql",
          "displayName": "PostgreSQL",
          "formatVersion": "1.0",
          "versions": [{ "versionRange": "*", "systemSchemas": ["pg_catalog", "information_schema", "pg_toast"], "platformSchemas": [], "platformExtensions": [] }]
        }
        """);
        WritePlatformFile("supabase.platform", """
        {
          "engine": "postgresql",
          "displayName": "Supabase",
          "formatVersion": "1.0",
          "detection": { "hostPatterns": ["*.supabase.co"] },
          "versions": [{ "versionRange": ">=15.0 <18.0", "systemSchemas": ["pg_catalog", "information_schema", "pg_toast"], "platformSchemas": ["auth", "storage"], "platformExtensions": [] }]
        }
        """);

        var resolver = CreateResolver();
        var result = resolver.Resolve("postgresql", "myproject.supabase.co", "18.1");

        result.DetectedPlatform.Should().Be("Supabase");
        result.VersionWarning.Should().NotBeNull();
        result.VersionWarning.Should().Contain("supabase.platform");
        result.VersionWarning.Should().Contain("18.1");

        // System schemas fall back to base engine
        result.SystemSchemas.Should().Contain("pg_catalog");
        result.SystemSchemas.Should().Contain("information_schema");

        // Platform schemas are NOT provided (unknown for this version)
        result.PlatformSchemas.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_CaseInsensitiveHostMatch()
    {
        WritePlatformFile("neon.platform", """
        {
          "engine": "postgresql",
          "displayName": "Neon",
          "formatVersion": "1.0",
          "detection": { "hostPatterns": ["*.neon.tech"] },
          "versions": [{ "versionRange": "*", "systemSchemas": ["pg_catalog"], "platformSchemas": [], "platformExtensions": ["neon"] }]
        }
        """);

        var resolver = CreateResolver();
        var result = resolver.Resolve("postgresql", "my-project.NEON.TECH", "16.0");

        result.DetectedPlatform.Should().Be("Neon");
        result.PlatformExtensions.Should().Contain("neon");
    }

    [Fact]
    public void Resolve_CorruptFile_SkippedGracefully()
    {
        WritePlatformFile("postgresql.platform", """
        {
          "engine": "postgresql",
          "displayName": "PostgreSQL",
          "formatVersion": "1.0",
          "versions": [{ "versionRange": "*", "systemSchemas": ["pg_catalog"], "platformSchemas": [], "platformExtensions": [] }]
        }
        """);
        WritePlatformFile("broken.platform", "NOT VALID JSON {{{");

        var resolver = CreateResolver();
        var result = resolver.Resolve("postgresql", "test.example.com", "15.0");

        // Should still resolve from the valid file
        result.SystemSchemas.Should().Contain("pg_catalog");
    }
}
