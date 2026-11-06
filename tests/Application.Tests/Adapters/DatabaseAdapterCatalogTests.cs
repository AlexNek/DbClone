using System.IO;

using DbClone.Application.Adapters;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

namespace Application.Tests.Adapters;

/// <summary>
/// Folder-driven discovery for <see cref="DatabaseAdapterCatalog"/>. These cases exercise the
/// paths a catalog meets before any engine is installed: nothing to read, an unreadable folder and
/// a folder that holds no adapter. Loading a real adapter (and so the duplicate-rejection and the
/// cross-context type-identity guarantees) needs a compiled plugin assembly and is covered by the
/// stub-adapter test that ships with Phase 7 (refactors/09 P9).
/// </summary>
public sealed class DatabaseAdapterCatalogTests : IDisposable
{
    private readonly string _root;

    public DatabaseAdapterCatalogTests()
        => _root = Directory.CreateTempSubdirectory("dbclone-catalog-").FullName;

    [Fact]
    public void MissingPluginDirectory_YieldsEmptyCatalogWithoutThrowing()
    {
        // Arrange — a path that does not exist is the normal state before an engine is installed.
        var missing = Path.Combine(_root, "does-not-exist");

        // Act
        var catalog = new DatabaseAdapterCatalog(missing, NullLoggerFactory.Instance);

        // Assert
        catalog.All.Should().BeEmpty();
        catalog.Formats.Should().BeEmpty();
        catalog.Platforms.Should().BeEmpty();
        catalog.Find("no-such-engine").Should().BeNull();
    }

    [Fact]
    public void EmptyPluginDirectory_YieldsEmptyCatalog()
    {
        // Arrange
        var plugins = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(plugins);

        // Act
        var catalog = new DatabaseAdapterCatalog(plugins, NullLoggerFactory.Instance);

        // Assert
        catalog.All.Should().BeEmpty();
    }

    [Fact]
    public void FolderWithNoAssembly_IsSkippedWithoutThrowing()
    {
        // Arrange — a subfolder that is not a publish output at all.
        var plugins = Path.Combine(_root, "plugins");
        var stray = Directory.CreateDirectory(Path.Combine(plugins, "readme")).FullName;
        File.WriteAllText(Path.Combine(stray, "note.txt"), "not a plugin");

        // Act
        var catalog = new DatabaseAdapterCatalog(plugins, NullLoggerFactory.Instance);

        // Assert
        catalog.All.Should().BeEmpty();
    }

    [Fact]
    public void FolderWithUnreadableAssembly_IsSkippedWithoutThrowing()
    {
        // Arrange — a dll/deps pair that looks like an entry assembly but cannot be loaded. A bad
        // image or a malformed deps file must be logged and skipped, never taken to the caller.
        var plugins = Path.Combine(_root, "plugins");
        var corrupt = Directory.CreateDirectory(Path.Combine(plugins, "corrupt")).FullName;
        File.WriteAllBytes(Path.Combine(corrupt, "Corrupt.dll"), [0x00, 0x01, 0x02, 0x03]);
        File.WriteAllText(Path.Combine(corrupt, "Corrupt.deps.json"), "{ not valid json");

        // Act
        var act = () => new DatabaseAdapterCatalog(plugins, NullLoggerFactory.Instance);

        // Assert
        act.Should().NotThrow();
        act().All.Should().BeEmpty();
    }

    [Fact]
    public void Find_IsCaseInsensitive_OnAnEmptyCatalogStillReturnsNull()
    {
        // Arrange
        var plugins = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(plugins);
        var catalog = new DatabaseAdapterCatalog(plugins, NullLoggerFactory.Instance);

        // Act / Assert
        catalog.Find("PostgreSQL").Should().BeNull();
        catalog.Find("postgresql").Should().BeNull();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temporary directory.
        }
    }
}
