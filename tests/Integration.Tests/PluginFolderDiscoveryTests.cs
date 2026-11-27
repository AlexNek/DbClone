using System.IO;

using DbClone.Application.Adapters;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

namespace Integration.Tests;

/// <summary>
/// The composition root's real job: a plugin folder is copied nowhere by hand, and the catalog finds,
/// loads and describes whatever engine is published into it. The folder under test is built from the
/// build output, so the test sees the same layout the installer ships.
/// </summary>
public sealed class PluginFolderDiscoveryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "dbclone-plugins-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public void CatalogLoadsTheEngineFromTheFolderItWasPublishedTo()
    {
        // Arrange — one folder per engine, holding the engine assembly and its platform data.
        var pluginFolder = Path.Combine(_root, "plugins", "postgresql");
        CopyToPluginFolder(pluginFolder);

        // Act
        var catalog = new DatabaseAdapterCatalog(
            Path.Combine(_root, "plugins"),
            NullLoggerFactory.Instance);

        // Assert
        catalog.All.Should().HaveCount(1);
        catalog.Find("postgresql").Should().NotBeNull();
        catalog.Find("mysql").Should().BeNull("selection is by dialect, never a fallback");
        catalog.All[0].Info.Dialect.Should().Be("postgresql");
        catalog.Formats.Should().NotBeEmpty("the connection-text services are built from this list");
        catalog.Platforms.Should().NotBeEmpty("the connection dialog's platform list comes from here");
    }

    [Fact]
    public void AFolderThatHoldsNoAdapterIsSkippedAndTheCatalogStillBuilds()
    {
        // Arrange — an empty engine folder is the state after a failed publish, not a crash.
        var folder = Path.Combine(_root, "plugins", "broken");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not an assembly");

        // Act
        var catalog = new DatabaseAdapterCatalog(
            Path.Combine(_root, "plugins"),
            NullLoggerFactory.Instance);

        // Assert
        catalog.All.Should().BeEmpty();
        catalog.Find("postgresql").Should().BeNull();
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The plugin assembly stays mapped by its load context, which is not collectible, so the
            // copied file cannot be deleted until the process ends. The OS cleans the temp folder.
        }
        catch (UnauthorizedAccessException)
        {
            // Same cause as above on Windows, which reports the lock as an access denial.
        }
    }

    /// <summary>
    /// Reproduces a published plugin folder: the engine assembly with the <c>.deps.json</c> a
    /// <c>dotnet publish</c> leaves beside it, the driver it needs, and the platform files it reads
    /// from <c>platforms/&lt;dialect&gt;</c> inside its own directory.
    /// </summary>
    private static void CopyToPluginFolder(string pluginFolder)
    {
        Directory.CreateDirectory(pluginFolder);

        foreach (var assembly in new[] { "DbAdapter.PostgreSql.dll", "Npgsql.dll" })
        {
            var source = Path.Combine(AppContext.BaseDirectory, assembly);
            if (File.Exists(source))
                File.Copy(source, Path.Combine(pluginFolder, assembly), overwrite: true);
        }

        // The catalog takes the one assembly a publish marked with a deps file as the entry point;
        // this is that marker, with no host-relative asset for the resolver to find.
        File.WriteAllText(
            Path.Combine(pluginFolder, "DbAdapter.PostgreSql.deps.json"),
            """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": { ".NETCoreApp,Version=v10.0": {} },
              "libraries": {}
            }
            """);

        var platforms = Path.Combine(AppContext.BaseDirectory, "platforms", "postgresql");
        if (!Directory.Exists(platforms)) return;

        var destination = Path.Combine(pluginFolder, "platforms", "postgresql");
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(platforms))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }
}
