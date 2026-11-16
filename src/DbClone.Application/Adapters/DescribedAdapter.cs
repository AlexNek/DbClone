using CanonicalModel.Platforms;

using DbAdapter.Definitions;

namespace DbClone.Application.Adapters;

/// <summary>
/// One plugin folder's adapter together with the contract values the catalog needs from it.
/// <para>
/// Reading those values runs a plugin's own code and can throw, so they are read once, inside a
/// guard, and every later step of catalog construction works from this snapshot. That is what
/// keeps a malformed plugin a skipped folder instead of a failed startup.
/// </para>
/// </summary>
/// <param name="Adapter">The constructed adapter.</param>
/// <param name="Folder">The folder it came from, so a warning can name it.</param>
/// <param name="Id">The adapter's internal id, read from <see cref="AdapterInfo.Id"/>.</param>
/// <param name="Dialect">The dialect a connection selects it by, read from <see cref="AdapterInfo.Dialect"/>.</param>
/// <param name="Formats">The connection-text formats the adapter offers.</param>
/// <param name="Platforms">The hosted platforms the adapter declares for its dialect.</param>
internal sealed record DescribedAdapter(
    IDatabaseAdapter Adapter,
    string Folder,
    string Id,
    string Dialect,
    IReadOnlyList<IConnectionTextFormat> Formats,
    IReadOnlyList<PlatformEntry> Platforms);
