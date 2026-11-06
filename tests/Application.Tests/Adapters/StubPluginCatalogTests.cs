using System.IO;

using CanonicalModel.DTOs;
using CanonicalModel.Models;
using CanonicalModel.Platforms;

using DbAdapter.Definitions;

using DbClone.Application.Adapters;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

namespace Application.Tests.Adapters;

/// <summary>
/// The P9 proof (refactors/09): a second "engine", compiled as the <c>DbAdapter.StubPlugin</c>
/// assembly and staged into a temporary plugin folder, is discovered by the catalog, selected by
/// its dialect and run to completion through the shared pipeline — with no production file touched.
/// The same run asserts the platform the catalog lists was read from the plugin's own <c>.platform</c>
/// file (not the application's folder), and that the loaded adapter implements the host's
/// <see cref="IDatabaseAdapter"/> — a single type identity across load contexts. These are exactly the
/// guarantees <see cref="DatabaseAdapterCatalogTests"/> defers to this test because they need a real,
/// loadable plugin assembly.
/// </summary>
public sealed class StubPluginCatalogTests : IDisposable
{
    private readonly string _root;

    public StubPluginCatalogTests()
        => _root = Directory.CreateTempSubdirectory("dbclone-stub-").FullName;

    [Fact]
    public async Task StubPlugin_IsDiscoveredSelectedByDialectReadsOwnPlatformFileAndRunsToCompletion()
    {
        // Arrange — stage the compiled stub (dll + deps.json) into a temp plugins/<dialect> folder
        // and drop a .platform file beside it. This is the whole "installation" of the engine.
        var pluginFolder = BuildPluginFolder();

        // Act
        var catalog = new DatabaseAdapterCatalog(pluginFolder, NullLoggerFactory.Instance);

        // Assert — discovery: the folder yielded exactly one adapter under its own dialect.
        catalog.All.Should().ContainSingle();
        var adapter = catalog.All[0];
        adapter.Info.Dialect.Should().Be("stubdialect");
        catalog.Find("STUBDIALECT").Should().BeSameAs(adapter); // selection is case-insensitive
        catalog.Find("no-such-engine").Should().BeNull();

        // Assert — single type identity: the loaded type came from a different assembly yet
        // implements the host's IDatabaseAdapter (a duplicate context type would fail this cast).
        adapter.Should().BeAssignableTo<IDatabaseAdapter>();
        adapter.GetType().Assembly.GetName().Name.Should().Be("DbAdapter.StubPlugin");
        adapter.GetType().Assembly.Should().NotBeSameAs(typeof(StubPluginCatalogTests).Assembly);

        // Assert — the plugin read its own data file: the catalog's platform list is exactly the
        // platform declared in the .platform file the test wrote into the plugin folder.
        catalog.Platforms.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PlatformEntry("stubhosted", "Stub Hosted"));

        // Assert — run to completion: the copy session drives the shared pipeline over the two stages.
        var request = new CopyRequest(
            new DatabaseTarget("stubdialect", "Host=test.example.com;Database=testdb"),
            new DatabaseTarget("stubdialect", "Host=test.example.com;Database=testdb"),
            EmptyModel(),
            new CopyOptions());

        await using var session = adapter.CreateCopySession();
        var result = await session.ExecuteAsync(request, progress: null, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.StageResults.Should().HaveCount(2);
        result.Errors.Should().BeEmpty();
    }

    /// <summary>
    /// Copies the staged stub assembly into <c>plugins/stubdialect/</c> and writes a
    /// <c>platforms/stubdialect/stub.platform</c> file, returning the plugins directory.
    /// </summary>
    private string BuildPluginFolder()
    {
        var staged = Path.Combine(AppContext.BaseDirectory, "stubplugin");
        var sourceDll = Path.Combine(staged, "DbAdapter.StubPlugin.dll");
        var sourceDeps = Path.Combine(staged, "DbAdapter.StubPlugin.deps.json");
        File.Exists(sourceDll).Should().BeTrue(
            $"the {sourceDll} must be staged by the build for the plugin-catalog proof to run");

        var plugins = Path.Combine(_root, "plugins");
        var dialect = Directory.CreateDirectory(Path.Combine(plugins, "stubdialect")).FullName;
        File.Copy(sourceDll, Path.Combine(dialect, "DbAdapter.StubPlugin.dll"));
        File.Copy(sourceDeps, Path.Combine(dialect, "DbAdapter.StubPlugin.deps.json"));

        var platformDir = Directory.CreateDirectory(Path.Combine(dialect, "platforms", "stubdialect")).FullName;
        File.WriteAllText(Path.Combine(platformDir, "stub.platform"), StubPlatformFile);
        return plugins;
    }

    private static DatabaseModel EmptyModel() => new(
        DatabaseName: "testdb",
        ServerVersion: "1.0.0",
        Schemas: [],
        Tables: [],
        Views: [],
        MaterializedViews: [],
        Sequences: [],
        Enums: [],
        Domains: [],
        CompositeTypes: [],
        Functions: [],
        Triggers: [],
        Policies: [],
        Publications: [],
        Subscriptions: [],
        Extensions: []);

    // Obviously-fake platform data (no real host, port, user or engine fact) — refactors/09 test rules.
    private const string StubPlatformFile = """
        {
          "id": "stubhosted",
          "engine": "stubdialect",
          "displayName": "Stub Hosted",
          "formatVersion": "1.0",
          "defaults": {
            "port": 1234,
            "sslMode": "Require",
            "username": "stub_user"
          },
          "versions": [
            {
              "versionRange": "*",
              "systemSchemas": [],
              "platformSchemas": [],
              "platformExtensions": []
            }
          ]
        }
        """;

    public void Dispose()
    {
        // The catalog loads the plugin into a non-collectible AssemblyLoadContext that lives for the
        // process, so the copied assembly file stays memory-mapped and Windows refuses to delete it
        // (an IOException whose inner/derived type is not always UnauthorizedAccessException). This is
        // a throwaway temp subdirectory, so cleanup is best-effort: try, swallow any failure, and let
        // the OS reclaim %TEMP%.
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception)
        {
            // Ignore — the locked plugin file cannot be removed until the process exits.
        }
    }
}
