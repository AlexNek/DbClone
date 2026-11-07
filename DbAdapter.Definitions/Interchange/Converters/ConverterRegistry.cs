using System.Reflection;

namespace DbAdapter.Definitions.Interchange.Converters;

/// <summary>
/// Registry that discovers and manages object converters.
/// Converters are loaded from plugin assemblies at startup.
/// </summary>
public sealed class ConverterRegistry
{
    private readonly List<IObjectConverter> _converters = [];
    private readonly Dictionary<(string Source, string Target), List<IObjectConverter>> _converterIndex = [];

    /// <summary>
    /// Gets the number of registered converters.
    /// </summary>
    public int Count => _converters.Count;

    /// <summary>
    /// Registers a converter instance.
    /// </summary>
    /// <param name="converter">The converter to register.</param>
    public void Register(IObjectConverter converter)
    {
        ArgumentNullException.ThrowIfNull(converter);

        _converters.Add(converter);

        var key = (converter.SourceDialect.ToLowerInvariant(), converter.TargetDialect.ToLowerInvariant());
        if (!_converterIndex.TryGetValue(key, out var list))
        {
            list = [];
            _converterIndex[key] = list;
        }

        list.Add(converter);
    }

    /// <summary>
    /// Discovers and registers all converters from the specified assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan for converters.</param>
    /// <remarks>
    /// Looks for public, non-abstract classes that implement <see cref="IObjectConverter"/>
    /// and have a parameterless constructor.
    /// </remarks>
    public void DiscoverFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var converterType = typeof(IObjectConverter);
        var types = assembly.GetExportedTypes()
            .Where(t => t.IsClass && !t.IsAbstract && converterType.IsAssignableFrom(t));

        foreach (var type in types)
        {
            try
            {
                // Try parameterless constructor first
                var constructor = type.GetConstructor(Type.EmptyTypes);
                if (constructor is not null)
                {
                    var instance = (IObjectConverter)constructor.Invoke(null);
                    Register(instance);
                    continue;
                }

                // If no parameterless constructor, skip with warning
                // (could be extended to support DI in the future)
                System.Diagnostics.Debug.WriteLine(
                    $"Converter {type.Name} has no parameterless constructor and was skipped.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to instantiate converter {type.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Discovers converters from all assemblies in the specified directory.
    /// </summary>
    /// <param name="pluginDirectory">The directory to scan for plugin assemblies.</param>
    public void DiscoverFromDirectory(string pluginDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        if (!Directory.Exists(pluginDirectory))
        {
            return;
        }

        foreach (var dllPath in Directory.EnumerateFiles(pluginDirectory, "*.dll", SearchOption.AllDirectories))
        {
            try
            {
                var assembly = Assembly.LoadFrom(dllPath);
                DiscoverFromAssembly(assembly);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to load assembly {dllPath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Gets a converter for the specified dialect pair and object kind.
    /// </summary>
    /// <param name="sourceDialect">The source dialect.</param>
    /// <param name="targetDialect">The target dialect.</param>
    /// <param name="objectKind">The kind of object to convert.</param>
    /// <returns>A converter that can handle the conversion, or null if none is available.</returns>
    /// <remarks>
    /// If multiple converters are available, returns the first one that supports the object kind.
    /// Converters are tried in registration order.
    /// </remarks>
    public IObjectConverter? GetConverter(string sourceDialect, string targetDialect, string objectKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDialect);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDialect);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKind);

        var key = (sourceDialect.ToLowerInvariant(), targetDialect.ToLowerInvariant());
        if (!_converterIndex.TryGetValue(key, out var converters))
        {
            return null;
        }

        foreach (var converter in converters)
        {
            if (converter.CanConvert(objectKind))
            {
                return converter;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets all converters for the specified dialect pair.
    /// </summary>
    /// <param name="sourceDialect">The source dialect.</param>
    /// <param name="targetDialect">The target dialect.</param>
    /// <returns>All converters that handle this dialect pair, or empty list if none.</returns>
    public IReadOnlyList<IObjectConverter> GetConverters(string sourceDialect, string targetDialect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDialect);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDialect);

        var key = (sourceDialect.ToLowerInvariant(), targetDialect.ToLowerInvariant());
        return _converterIndex.TryGetValue(key, out var converters)
            ? converters.AsReadOnly()
            : [];
    }

    /// <summary>
    /// Checks if a converter is available for the specified dialect pair and object kind.
    /// </summary>
    public bool HasConverter(string sourceDialect, string targetDialect, string objectKind)
    {
        return GetConverter(sourceDialect, targetDialect, objectKind) is not null;
    }

    /// <summary>
    /// Gets all registered dialect pairs.
    /// </summary>
    public IReadOnlyCollection<(string Source, string Target)> GetSupportedDialectPairs()
    {
        return _converterIndex.Keys.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets all registered object kinds for a dialect pair.
    /// </summary>
    public IReadOnlySet<string> GetSupportedObjectKinds(string sourceDialect, string targetDialect)
    {
        var converters = GetConverters(sourceDialect, targetDialect);
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var converter in converters)
        {
            foreach (var kind in converter.SupportedObjectKinds)
            {
                kinds.Add(kind);
            }
        }

        return kinds;
    }
}
