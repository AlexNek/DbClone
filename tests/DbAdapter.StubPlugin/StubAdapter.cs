using CanonicalModel.DTOs;
using CanonicalModel.Enums;
using CanonicalModel.Models;
using CanonicalModel.Platforms;

using DbAdapter.Definitions;

using Microsoft.Extensions.Logging;

namespace DbAdapter.StubPlugin;

/// <summary>
/// The P9 proof adapter (refactors/09). A second "engine" that ships as nothing but this assembly,
/// so the plugin catalog can be shown to discover an adapter from a folder, select it by its dialect
/// and run a copy to completion without any change to a production file. It holds in-memory metadata
/// and two pipeline stages — no SQL, no driver — and reads its platform data from the folder it was
/// loaded from (the same self-contained composition a real block uses).
/// <para>
/// Only the surfaces the proof touches are implemented: identity, the platform role (which reads the
/// plugin's own <c>.platform</c> file), the connection-text role (the catalog enumerates its formats)
/// and the copy session (which drives the shared <see cref="CopyPipeline"/>). The per-target admin,
/// cleaner and comparison roles are out of the proof's scope and refuse the call.
/// </para>
/// </summary>
public sealed class StubAdapter : IDatabaseAdapter
{
    /// <summary>The dialect this stub claims — the value a connection selects on. Distinct from "postgresql".</summary>
    public const string Dialect = "stubdialect";

    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// The constructor convention the catalog reflects over: a logger factory and the folder the
    /// assembly was loaded from. Nothing else — no container, no host type — so the plugin is loadable
    /// by an application that does not know this type at compile time.
    /// </summary>
    public StubAdapter(ILoggerFactory loggerFactory, string pluginDirectory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        Platform = new StubPlatformResolver(loggerFactory, pluginDirectory);
        Syntax = new StubSyntax();
        Info = new AdapterInfo(
            Id: "org.dbclone.stub",
            Dialect: Dialect,
            EngineVersionRange: "*",
            AdapterVersion: typeof(StubAdapter).Assembly.GetName().Version?.ToString() ?? "0.0.0");
    }

    /// <inheritdoc />
    public AdapterInfo Info { get; }

    /// <inheritdoc />
    public string ProviderName => "Stub Engine";

    /// <inheritdoc />
    public IConnectionStringSyntax Syntax { get; }

    /// <inheritdoc />
    public IPlatformResolver Platform { get; }

    /// <inheritdoc />
    public IMetadataSource CreateMetadataSource(DatabaseTarget target) =>
        new StubMetadataSource();

    /// <inheritdoc />
    public IDatabaseAdmin CreateAdmin(DatabaseTarget target) =>
        throw new NotSupportedException("The P9 proof does not exercise the administration role.");

    /// <inheritdoc />
    public IDatabaseCleaner CreateCleaner(DatabaseTarget target) =>
        throw new NotSupportedException("The P9 proof does not exercise the cleaning role.");

    /// <inheritdoc />
    public ITableComparer CreateComparer() =>
        throw new NotSupportedException("The P9 proof does not exercise the comparison role.");

    /// <inheritdoc />
    public IReadOnlyList<IModelComparer> CreateModelComparers() => [];

    /// <inheritdoc />
    public ICopySession CreateCopySession() => new StubCopySession(_loggerFactory);

    /// <summary>
    /// The connection-text role. The catalog only enumerates <see cref="Formats"/> (which is empty — the
    /// stub registers no import/export format), so the remaining members are inert but must exist.
    /// </summary>
    private sealed class StubSyntax : IConnectionStringSyntax
    {
        public IReadOnlyList<IConnectionTextFormat> Formats { get; } = [];

        public IReadOnlyList<string> SecurityOptions { get; } = ["Disable", "Require"];

        public string DefaultFormatId => "stub-default";

        public string BuildKeyValue(ConnectionStringFields fields) => string.Empty;

        public ESslMode ToSslMode(string identifier) =>
            identifier == "Require" ? ESslMode.Require : ESslMode.Disable;

        public bool TryParse(string value, out ConnectionStringFields fields)
        {
            fields = default!;
            return false;
        }

        public bool TryParseKeyValue(string value, out ConnectionStringFields fields)
        {
            fields = default!;
            return false;
        }
    }

    /// <summary>
    /// The platform role, reading <c>.platform</c> files from <c>&lt;pluginDirectory&gt;/platforms/&lt;dialect&gt;</c>
    /// — the folder the catalog handed this adapter, never the application's folder. The proof asserts the
    /// platform the catalog lists comes from that file (refactors/09 P3/P9).
    /// </summary>
    private sealed class StubPlatformResolver : IPlatformResolver
    {
        private readonly PlatformDefinitionLoader _loader;

        public StubPlatformResolver(ILoggerFactory loggerFactory, string pluginDirectory)
        {
            var platformsDir = Path.Combine(pluginDirectory, "platforms", Dialect);
            _loader = new PlatformDefinitionLoader(
                platformsDir,
                loggerFactory.CreateLogger<PlatformDefinitionLoader>());
        }

        public PlatformResolution Resolve(string engine, string host, string serverVersion) =>
            PlatformResolution.None;

        public string? DetectPlatformId(string engine, string host) => null;

        public PlatformDefaults GetConnectionDefaults(string engine, string? platformId)
        {
            var def = Find(platformId);
            return def?.Defaults ?? new PlatformDefaults(0, "Disable", "stub_user");
        }

        public IReadOnlyList<PlatformEntry> GetAllPlatforms(string engine) =>
            _loader.GetAll()
                .Select(def => new PlatformEntry(def.StableId, def.DisplayName))
                .ToList();

        private PlatformDefinition? Find(string? platformId)
        {
            var defs = _loader.GetAll();
            if (platformId is null)
            {
                return defs.FirstOrDefault();
            }

            return defs.FirstOrDefault(def => string.Equals(def.StableId, platformId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>An in-memory metadata source with no tables — enough to honour "in-memory metadata".</summary>
    private sealed class StubMetadataSource : IMetadataSource
    {
        public Task<List<(string Schema, string Name)>> GetTablesAsync(CancellationToken ct) =>
            Task.FromResult(new List<(string, string)>());

        public Task<IReadOnlyList<TableSizeInfo>> GetTableSizesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TableSizeInfo>>([]);

        public Task<DatabaseModel> ReadDatabaseModelAsync(bool excludePlatformSchemas = false, CancellationToken ct = default) =>
            Task.FromResult(EmptyModel());

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
    }

    /// <summary>
    /// A copy session that runs the shared <see cref="CopyPipeline"/> over two trivial stages. It supplies
    /// stages, not a runner — the point of moving the loop into the definitions (refactors/09 P5).
    /// </summary>
    private sealed class StubCopySession : ICopySession
    {
        private readonly ILoggerFactory _loggerFactory;

        public StubCopySession(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

        public async Task<CopyResult> ExecuteAsync(
            CopyRequest request,
            IProgress<CopyProgress>? progress,
            CancellationToken cancellationToken)
        {
            var pipeline = new CopyPipeline(
                new ICopyStage[] { new StubStage(ECopyStage.CreateViews, 100), new StubStage(ECopyStage.CreateTriggers, 110) },
                _loggerFactory.CreateLogger<CopyPipeline>());

            var context = new CopyContext
            {
                Request = request,
                Progress = progress,
                SourceModel = request.Model
            };

            return await pipeline.ExecuteAsync(context, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A pipeline stage that records it ran and succeeds — no work, no database.</summary>
    private sealed class StubStage : ICopyStage
    {
        private readonly int _order;

        public StubStage(ECopyStage name, int order)
        {
            Name = name;
            _order = order;
        }

        public ECopyStage Name { get; }

        public int Order => _order;

        public Task<StageResult> ExecuteAsync(CopyContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StageResult(Name, Success: true, TimeSpan.FromMilliseconds(1), ObjectsProcessed: 0, Details: []));
    }
}
