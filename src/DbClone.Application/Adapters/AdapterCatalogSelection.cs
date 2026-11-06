using DbAdapter.Definitions;

using DbClone.Application.Exceptions;

namespace DbClone.Application.Adapters;

/// <summary>
/// The single selection rule that maps a connection's engine value to an installed adapter
/// (refactors/09 P4). A blank value means "whatever is installed": the sole adapter serves it,
/// which is today's behaviour with files saved before the engine was recorded; zero or several
/// make the choice undecidable and are reported, never guessed. A named value resolves through
/// <see cref="IAdapterCatalog.Find"/>, and an unknown name is a reported error rather than a
/// silent fallback to some default engine.
/// </summary>
public static class AdapterCatalogSelection
{
    /// <summary>
    /// Resolves the adapter that serves a connection whose <c>Engine</c> is <paramref name="engine"/>,
    /// throwing <see cref="EngineSelectionException"/> when no single installed adapter matches.
    /// </summary>
    public static IDatabaseAdapter Resolve(this IAdapterCatalog catalog, string? engine)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (!string.IsNullOrWhiteSpace(engine))
        {
            return catalog.Find(engine)
                   ?? throw new EngineSelectionException(
                       $@"No installed database engine matches '{engine}'. Installed: {Describe(catalog)}.");
        }

        return catalog.All.Count switch
        {
            0 => throw new EngineSelectionException(
                "No database engine is installed. Install an engine before using this connection."),
            1 => catalog.All[0],
            _ => throw new EngineSelectionException(
                $@"This connection does not name an engine and more than one is installed ({Describe(catalog)}); choose one.")
        };
    }

    private static string Describe(IAdapterCatalog catalog) =>
        catalog.All.Count == 0
            ? "none"
            : string.Join(", ", catalog.All.Select(adapter => adapter.Info.Dialect));
}
