using System.Text;
using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// The cluster selector: one button per schema, per cyclic group and for the tables with no relation
/// at all. Every chip opens switched on, and switching one off hides its tables and their incident
/// edges without removing anything from the document (FR15, D8a).
/// </summary>
/// <remarks>
/// <para>
/// Which tables a chip claims was decided by the analyzer part; this writer only prints the buttons
/// and the claim sets the script reads. A chip group the catalog produced no chip for is not
/// written at all, so an empty heading never appears.
/// </para>
/// <para>
/// A schema the catalog holds only one of is stated, not offered: a lone chip's other state is
/// "every table hidden", so it is not a choice (FR15, pass 5). The isolating gesture the script adds
/// is announced in one hint, and only where a group actually holds two chips or more.
/// </para>
/// </remarks>
internal sealed class GraphClusterBarWriter
{
    private readonly CatalogGraphModel _model;
    private readonly ClusterSwitchSet _switches;

    /// <summary>Creates the writer for one document.</summary>
    /// <param name="model">The analyzed graph, with its chips and their claim sets.</param>
    public GraphClusterBarWriter(CatalogGraphModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        _switches = new ClusterSwitchSet(model);
    }

    /// <summary>Writes the cluster bar.</summary>
    /// <param name="html">The document being built.</param>
    public void WriteTo(StringBuilder html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var schemas = ChipsOf(EClusterChipKind.Schema);
        var cyclicGroups = ChipsOf(EClusterChipKind.CyclicGroup);
        var unconnected = ChipsOf(EClusterChipKind.Unconnected);

        html.Append("<section id=\"chips\" class=\"chips\">\n");
        WriteSchemas(html, schemas);
        WriteGroup(html, cyclicGroups, "cyclic group");
        WriteGroup(html, unconnected, "unconnected");
        if (schemas.Length > 1 || cyclicGroups.Length > 1 || unconnected.Length > 1)
        {
            html.Append("<span class=\"hint\">a click hides a cluster \u00b7 alt-click shows only it</span>\n");
        }

        html.Append("</section>\n");
    }

    private CatalogClusterChip[] ChipsOf(EClusterChipKind kind) =>
        [.. _model.Chips.Where(chip => chip.Kind == kind)];

    /// <summary>
    /// One schema needs no switch — the only thing its chip could do is hide every table at once —
    /// so the bar states it; two or more are a real choice and stay buttons (FR15).
    /// </summary>
    private void WriteSchemas(StringBuilder html, CatalogClusterChip[] chips)
    {
        if (chips.Length == 0)
        {
            return;
        }

        if (_switches.IsSwitch(chips[0].Index))
        {
            WriteGroup(html, chips, "schema");
            return;
        }

        html.Append("<div class=\"group\"><span>schema</span>\n<b>")
            .Append(GraphHtmlText.Escape(Label(chips[0]))).Append("</b>\n</div>\n");
    }

    private void WriteGroup(StringBuilder html, CatalogClusterChip[] chips, string caption)
    {
        if (chips.Length == 0)
        {
            return;
        }

        html.Append("<div class=\"group\">");
        if (chips.Length > 1 || !string.Equals(chips[0].Label, caption, StringComparison.OrdinalIgnoreCase))
        {
            html.Append("<span>").Append(GraphHtmlText.Escape(caption)).Append("</span>\n");
        }

        foreach (var chip in chips)
        {
            html.Append("<button type=\"button\" class=\"chip on\" data-c=\"").Append(chip.Index)
                .Append("\" aria-pressed=\"true\">").Append(GraphHtmlText.Escape(Label(chip)))
                .Append("</button>\n");
        }

        html.Append("</div>\n");
    }

    /// <summary>
    /// A cyclic group's chip names its first table and counts the rest, because "↺ 9" alone does not
    /// tell a reader which tables they are about to hide.
    /// </summary>
    private string Label(CatalogClusterChip chip)
    {
        if (chip.Kind != EClusterChipKind.CyclicGroup || chip.MemberNodeIndexes.Count == 0)
        {
            return chip.Label;
        }

        var first = _model.Nodes[chip.MemberNodeIndexes[0]].DisplayName;
        var others = chip.MemberNodeIndexes.Count - 1;
        return others == 0 ? $"{chip.Label} \u00b7 {first}" : $"{chip.Label} \u00b7 {first} +{others}";
    }
}
