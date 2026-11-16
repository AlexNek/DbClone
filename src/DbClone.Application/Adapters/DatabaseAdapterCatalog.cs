using System.Reflection;

using CanonicalModel.Platforms;

using DbAdapter.Definitions;

using Microsoft.Extensions.Logging;

namespace DbClone.Application.Adapters;

/// <summary>
/// Reads the plugin folder once and turns it into the installed adapter set. Each immediate
/// subfolder of <c>plugins/</c> is one engine: it is loaded into its own
/// <see cref="PluginAssemblyLoadContext"/>, its entry assembly is scanned for a concrete
/// <see cref="IDatabaseAdapter"/> with the agreed constructor, and adapters whose dialect or id
/// collide are refused. A folder that is empty, unreadable or holds no adapter is logged and
/// skipped, never fatal — an empty catalog is a supported state.
/// </summary>
public sealed class DatabaseAdapterCatalog : IAdapterCatalog
{
    private readonly ILogger _logger;

    private readonly IReadOnlyDictionary<string, IDatabaseAdapter> _byDialect;

    /// <param name="pluginDirectory">The folder holding one subfolder per engine (typically <c>AppContext.BaseDirectory/plugins</c>).</param>
    /// <param name="loggerFactory">Logger factory the catalog warns through, and that is handed to every adapter.</param>
    public DatabaseAdapterCatalog(string pluginDirectory, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<DatabaseAdapterCatalog>();

        var loaded = Discover(pluginDirectory, loggerFactory, _logger);
        var described = DescribeAdapters(loaded, _logger);
        var accepted = AcceptNonDuplicates(described, _logger);

        All = accepted.Select(entry => entry.Adapter).ToList();
        _byDialect = accepted.ToDictionary(
            entry => entry.Dialect,
            entry => entry.Adapter,
            StringComparer.OrdinalIgnoreCase);

        if (All.Count == 0)
            _logger.LogError("No database adapters were loaded from '{Directory}'. Database operations are unavailable until a plugin is installed.", pluginDirectory);

        Formats = accepted
            .SelectMany(entry => entry.Formats)
            .OrderBy(format => format.DetectionPriority)
            .ThenBy(format => format.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Platforms = accepted
            .OrderBy(entry => entry.Dialect, StringComparer.OrdinalIgnoreCase)
            .SelectMany(entry =>
                entry.Platforms.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<IDatabaseAdapter> All { get; }

    /// <inheritdoc />
    public IReadOnlyList<IConnectionTextFormat> Formats { get; }

    /// <inheritdoc />
    public IReadOnlyList<PlatformEntry> Platforms { get; }

    /// <inheritdoc />
    public IDatabaseAdapter? Find(string dialect) =>
        dialect is not null && _byDialect.TryGetValue(dialect, out var adapter) ? adapter : null;

    /// <summary>
    /// Scans <paramref name="pluginDirectory"/> and returns one entry per folder that produced an
    /// adapter, paired with the folder it came from. A missing directory yields an empty result.
    /// </summary>
    private static IReadOnlyList<(IDatabaseAdapter Adapter, string Folder)> Discover(
        string pluginDirectory,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        var found = new List<(IDatabaseAdapter, string)>();
        if (!Directory.Exists(pluginDirectory))
        {
            logger.LogWarning("No plugin directory at '{Directory}'; the engine catalog is empty.", pluginDirectory);
            return found;
        }

        foreach (var folder in Directory.EnumerateDirectories(pluginDirectory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var adapter = LoadFolder(folder, loggerFactory, logger);
                if (adapter is not null)
                {
                    found.Add((adapter, folder));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping plugin folder '{Folder}': it could not be loaded.", folder);
            }
        }

        return found;
    }

    /// <summary>
    /// Loads a single plugin folder. Returns <see langword="null"/> when the folder has no managed
    /// entry assembly, no adapter type, or cannot be constructed — each reason logged as a warning.
    /// </summary>
    private static IDatabaseAdapter? LoadFolder(string folder, ILoggerFactory loggerFactory, ILogger logger)
    {
        var entryAssembly = FindEntryAssembly(folder);
        if (entryAssembly is null)
        {
            logger.LogWarning("No plugin assembly found in '{Folder}'; skipping.", folder);
            return null;
        }

        var context = new PluginAssemblyLoadContext(entryAssembly);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(entryAssembly);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Assembly '{Assembly}' in '{Folder}' could not be loaded; skipping.", Path.GetFileName(entryAssembly), folder);
            return null;
        }

        var adapterType = TryFindAdapterType(assembly, logger, folder);
        if (adapterType is null)
        {
            return null;
        }

        object? instance;
        try
        {
            instance = Activator.CreateInstance(adapterType, loggerFactory, folder);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Adapter '{Type}' in '{Folder}' could not be constructed; skipping.", adapterType.FullName, folder);
            return null;
        }

        var adapter = (IDatabaseAdapter)instance!;
        if (string.IsNullOrWhiteSpace(adapter.Info.Dialect))
        {
            logger.LogWarning("Adapter '{Type}' in '{Folder}' declares no dialect; skipping.", adapterType.FullName, folder);
            return null;
        }

        return adapter;
    }

    /// <summary>
    /// The folder's primary assembly: the one <c>.dll</c> that has a sibling <c>.deps.json</c> of the
    /// same name — the marker a <c>dotnet publish</c> leaves on the entry assembly and only on it, so
    /// the driver and other copied dependencies are not mistaken for the plugin itself.
    /// </summary>
    private static string? FindEntryAssembly(string folder) =>
        Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(dll => File.Exists(Path.ChangeExtension(dll, ".deps.json")))
            .OrderBy(dll => dll, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>
    /// The first concrete, non-abstract <see cref="IDatabaseAdapter"/> with the public
    /// <c>(ILoggerFactory, string)</c> constructor, or <see langword="null"/> when the assembly has
    /// none. A type only qualifies when it implements the host's interface — the single type identity
    /// the load context is built to preserve.
    /// </summary>
    private static Type? TryFindAdapterType(Assembly assembly, ILogger logger, string folder)
    {
        Type?[] candidates;
        try
        {
            candidates = assembly.GetTypes().Select(IsAdapterType).ToArray();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A dependency of some types may not resolve; scan the types that did.
            candidates = ex.Types.Select(IsAdapterType).ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Types in '{Folder}' could not be enumerated; skipping.", folder);
            return null;
        }

        var adapterType = Array.Find(candidates, t => t is not null);
        if (adapterType is null)
        {
            logger.LogWarning("No database adapter type found in '{Folder}'; skipping.", folder);
        }

        return adapterType;

        static Type? IsAdapterType(Type? type) =>
            type is not null
            && type.IsClass
            && !type.IsAbstract
            && typeof(IDatabaseAdapter).IsAssignableFrom(type)
            && type.GetConstructor(new[] { typeof(ILoggerFactory), typeof(string) }) is not null
                ? type
                : null;
    }

    /// <summary>
    /// Reads every contract value the catalog needs from each loaded adapter. A plugin that
    /// throws while answering one of them is dropped here — logged with its folder — so a
    /// malformed plugin can never take the catalog, and with it the application, down.
    /// </summary>
    internal static IReadOnlyList<DescribedAdapter> DescribeAdapters(
        IReadOnlyList<(IDatabaseAdapter Adapter, string Folder)> loaded,
        ILogger logger)
    {
        var described = new List<DescribedAdapter>(loaded.Count);

        foreach (var (adapter, folder) in loaded)
        {
            try
            {
                var info = adapter.Info;
                described.Add(
                    new DescribedAdapter(
                        adapter,
                        folder,
                        info.Id,
                        info.Dialect,
                        adapter.Syntax.Formats.ToList(),
                        adapter.Platform.GetAllPlatforms(info.Dialect).ToList()));
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Adapter in '{Folder}' could not report its contract values; skipping.",
                    folder);
            }
        }

        return described;
    }

    /// <summary>
    /// Drops every adapter that collides with another — on dialect, on internal id, or on a hosted
    /// platform id — and reports the folders behind each collision. A duplicate is an installation
    /// mistake; silently choosing one would make behaviour depend on folder order.
    /// </summary>
    private static IReadOnlyList<DescribedAdapter> AcceptNonDuplicates(
        IReadOnlyList<DescribedAdapter> loaded,
        ILogger logger)
    {
        var rejectedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Adapters may not share a dialect (the value a connection selects on) or an internal id
        // (the value logs and diagnostics reference); either collision names two equal-looking engines.
        RejectCollisions(loaded, rejectedFolders, logger, entry => entry.Dialect, "dialect");
        RejectCollisions(loaded, rejectedFolders, logger, entry => entry.Id, "adapter id");

        // A hosted platform id must belong to exactly one adapter, or the dropdown shows two entries
        // that resolve to different engines by load order.
        var platformOwners = new List<(string PlatformId, string Folder)>();
        foreach (var entry in loaded)
        {
            foreach (var platform in entry.Platforms)
            {
                platformOwners.Add((platform.Id, entry.Folder));
            }
        }

        foreach (var group in platformOwners.GroupBy(p => p.PlatformId, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(p => p.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            var folders = group.Select(p => p.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            logger.LogError(
                "Platform id '{PlatformId}' is declared by more than one plugin folder ({Folders}); neither engine was loaded.",
                group.Key,
                string.Join(", ", folders));
            foreach (var folder in folders)
            {
                rejectedFolders.Add(folder);
            }
        }

        return loaded
            .Where(entry => !rejectedFolders.Contains(entry.Folder))
            .OrderBy(entry => entry.Dialect, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Groups <paramref name="loaded"/> by <paramref name="keySelector"/> and rejects every folder in a
    /// group claimed by more than one folder, logging the collision under <paramref name="noun"/>.
    /// </summary>
    private static void RejectCollisions(
        IReadOnlyList<DescribedAdapter> loaded,
        HashSet<string> rejectedFolders,
        ILogger logger,
        Func<DescribedAdapter, string> keySelector,
        string noun)
    {
        foreach (var group in loaded.GroupBy(keySelector, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var folders = group.Select(entry => entry.Folder).ToList();
            logger.LogError(
                "{Noun} '{Value}' is claimed by more than one plugin folder ({Folders}); none of them were loaded.",
                noun,
                group.Key,
                string.Join(", ", folders));
            foreach (var folder in folders)
            {
                rejectedFolders.Add(folder);
            }
        }
    }
}
