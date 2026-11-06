using CanonicalModel.Platforms;

using DbAdapter.Definitions;

namespace DbClone.Application.Adapters;

/// <summary>
/// The application's single route to every installed database engine. The catalog is filled
/// once, at startup, by reading the plugin folder; it is the only place a folder is turned into
/// an <see cref="IDatabaseAdapter"/>. Nothing else names an engine (refactors/09 P3).
/// </summary>
public interface IAdapterCatalog
{
    /// <summary>Every adapter found in the plugin folder, in a stable order. Empty when none is installed.</summary>
    IReadOnlyList<IDatabaseAdapter> All { get; }

    /// <summary>
    /// The adapter whose dialect matches <paramref name="dialect"/>, or <see langword="null"/> when
    /// no installed adapter speaks it. Matching is case-insensitive.
    /// </summary>
    IDatabaseAdapter? Find(string dialect);

    /// <summary>
    /// The union of every adapter's connection-text formats, ordered by detection priority then id,
    /// so pasted text of any installed engine is recognised without the caller naming one.
    /// </summary>
    IReadOnlyList<IConnectionTextFormat> Formats { get; }

    /// <summary>
    /// The union of every adapter's hosted-platform entries, ordered by dialect then platform id.
    /// </summary>
    IReadOnlyList<PlatformEntry> Platforms { get; }
}
