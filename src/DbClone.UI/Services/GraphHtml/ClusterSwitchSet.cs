using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// Which chips are printed as switches, and therefore which claims a table carries into the page.
/// </summary>
/// <remarks>
/// <para>
/// One rule, asked by both writers so they cannot disagree: a lone schema chip is <b>not</b> a switch.
/// Its only other state is "every table hidden", so the bar states the schema instead of offering it
/// (FR15, pass 5).
/// </para>
/// <para>
/// The claim side matters as much as the button side. A table claimed by a chip that can never be
/// turned off stays visible for ever, because the page shows a table while <em>any</em> of its claims is
/// on — so a schema that is only stated must not be emitted as a claim, or the unconnected switch would
/// dim, unpress and change nothing. The analyzer's own claim map keeps it (T23); this is the printing
/// decision.
/// </para>
/// </remarks>
internal sealed class ClusterSwitchSet
{
    private readonly HashSet<int> _switchable;

    /// <summary>Reads the chips a page will print as switches.</summary>
    /// <param name="model">The analyzed graph with its chips.</param>
    public ClusterSwitchSet(CatalogGraphModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var schemas = model.Chips.Count(chip => chip.Kind == EClusterChipKind.Schema);
        _switchable =
        [
            .. model.Chips
                .Where(chip => chip.Kind != EClusterChipKind.Schema || schemas > 1)
                .Select(chip => chip.Index),
        ];
    }

    /// <summary>Whether a chip is printed as a button a reader can switch.</summary>
    /// <param name="chipIndex">The chip's index in the analyzer's list.</param>
    /// <returns><see langword="true"/> when the chip can be turned off in the page.</returns>
    public bool IsSwitch(int chipIndex) => _switchable.Contains(chipIndex);

    /// <summary>The claims of one table, in the order the analyzer stated them, switches only.</summary>
    /// <param name="claims">The chips the analyzer gave this table.</param>
    /// <returns>The chips the page may actually toggle for it.</returns>
    public IEnumerable<int> Switchable(IReadOnlyList<int> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        return claims.Where(IsSwitch);
    }
}
