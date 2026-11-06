using System.Reflection;
using System.Runtime.Loader;

namespace DbClone.Application.Adapters;

/// <summary>
/// Loads one plugin folder into its own, isolated assembly context so its private dependencies
/// (the driver, for example) resolve from beside the plugin rather than from the application.
/// <para>
/// Shared contract assemblies — <c>DbAdapter.Definitions</c>, <c>CanonicalModel</c> and the
/// <c>Microsoft.Extensions.*</c> family — are deliberately <em>not</em> loaded here. Returning
/// <see langword="null"/> from <see cref="Load"/> lets the runtime bind them from the default
/// context, which is what guarantees a loaded adapter implements the host's own
/// <see cref="DbAdapter.Definitions.IDatabaseAdapter"/> interface (a single type identity across
/// contexts, refactors/09 P2).
/// </para>
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedContractPrefixes = ["DbAdapter.Definitions", "CanonicalModel", "Microsoft.Extensions."];

    private readonly AssemblyDependencyResolver _resolver;

    /// <param name="entryAssemblyPath">Full path to the plugin's primary assembly, whose <c>.deps.json</c> drives the resolver.</param>
    public PluginAssemblyLoadContext(string entryAssemblyPath)
        : base(isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(entryAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null || IsSharedContract(name))
        {
            // Fall through to the default context — the host owns these types.
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    private static bool IsSharedContract(string assemblyName) =>
        Array.Exists(SharedContractPrefixes, prefix =>
            assemblyName.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
