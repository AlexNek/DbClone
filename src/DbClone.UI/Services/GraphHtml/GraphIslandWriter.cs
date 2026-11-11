using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbClone.Application.Interchange;
using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// The data island: every fact the script needs, written as JSON in a non-executable script element
/// so the picture can be searched, focused and described without a second parse of the catalog.
/// </summary>
/// <remarks>
/// <para>
/// The island is serialized with the default <see cref="JsonSerializerOptions"/> encoder, which
/// escapes <c>&lt;</c> as <c>\u003C</c> — so a table called <c>&lt;/script&gt;</c> cannot end the
/// element it is written inside (D11, FR8).
/// </para>
/// <para>
/// Entries are addressed by node index, never by name, so the script never has to trust or match a
/// string the catalog supplied (D3, FR4b).
/// </para>
/// </remarks>
internal sealed class GraphIslandWriter
{
    private static readonly JsonSerializerOptions IslandJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly CatalogGraphView _view;
    private readonly CatalogGraphModel _model;

    /// <summary>Creates the writer for one document.</summary>
    /// <param name="view">The catalog as read back, for the facts the graph itself does not carry.</param>
    /// <param name="model">The analyzed graph.</param>
    public GraphIslandWriter(CatalogGraphView view, CatalogGraphModel model)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(model);

        _view = view;
        _model = model;
    }

    /// <summary>Writes the island.</summary>
    /// <param name="html">The document being built.</param>
    public void WriteTo(StringBuilder html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var payload = new
        {
            tables = _model.TableCount,
            relations = _model.RelationCount,
            hasData = _model.HasData,
            source = _view.SourceDialect,
            nodes = NodeRows(),
            edges = EdgeRows(),
            details = DetailRows(),
            chips = ChipRows(),
            search = SearchRows(),
        };

        html.Append("<script type=\"application/json\" id=\"data\">")
            .Append(JsonSerializer.Serialize(payload, IslandJson))
            .Append("</script>\n");
    }

    private object[] NodeRows() =>
        [.. _model.Nodes.Select(node => new
        {
            i = node.Index,
            name = node.DisplayName,
            schema = node.Schema,
            cap = node.IsCaptured,
            rows = node.RowCount,
            g = node.CyclicGroupNumber,
            // The words naming a link table, marked as an inference by the panel and the tooltip. A
            // table that is not one carries no key at all, so nothing has to be said about it (D14.3).
            join = node.JoinTableBetween,
        })];

    private object[] EdgeRows() =>
        [.. _model.Edges.Select(edge => new
        {
            i = edge.Index,
            c = edge.ChildIndex,
            p = edge.ParentIndex,
            name = edge.Name,
            label = edge.Label,
            from = _model.Nodes[edge.ChildIndex].DisplayName,
            to = _model.Nodes[edge.ParentIndex].DisplayName,
            cols = edge.Columns,
            refs = edge.ReferencedColumns,
            del = edge.OnDelete,
            upd = edge.OnUpdate,
            mand = edge.IsMandatory,
            self = edge.IsSelfReference,
            // The shape of the relation at the child end: `one` says at most one child row names a
            // parent row and `exact` says the child row cannot be absent either (D14.1, D14.2).
            one = edge.IsOneToOne,
            exact = edge.IsExactOne,
        })];

    private object[] DetailRows() =>
        [.. _model.Nodes.Select(node => Detail(node))];

    private object Detail(CatalogGraphNode node)
    {
        var detail = _model.DetailsByNode[node.Index];
        return new
        {
            i = node.Index,
            name = detail.QualifiedName,
            cap = detail.IsCaptured,
            rows = detail.RowCount,
            outE = detail.OutgoingEdgeIndexes,
            inE = detail.IncomingEdgeIndexes,
            nei = detail.NeighbourNodeIndexes,
            hop2 = detail.TwoHopNodeIndexes,
            g = detail.CyclicGroupNumber,
            members = detail.CyclicGroupMembers,
            cols = detail.Columns.Select(column => new
            {
                name = column.Name,
                logicalType = column.LogicalType,
                nullable = column.IsNullable,
                pk = column.IsPartOfPrimaryKey,
                fk = column.IsReferencingForeignKey,
                refd = column.IsReferencedByForeignKey,
                comment = column.Comment,
            }).ToArray(),
            // The table's own definitions, which the panel states itself rather than pointing the
            // reader at the catalog's JSON (D10). A table the catalog does not hold carries none of
            // this, so the panel says nothing it did not read (FR4).
            pk = detail.PrimaryKey is null
                ? null
                : new { name = detail.PrimaryKey.Name, cols = detail.PrimaryKey.Columns },
            uq = detail.UniqueKeys.Select(key => new { name = key.Name, cols = key.Columns }).ToArray(),
            idx = detail.Indexes.Select(index => new
            {
                name = index.Name,
                unique = index.IsUnique,
                cols = index.Columns,
                def = index.Definition,
            }).ToArray(),
            ck = detail.CheckConstraints.Select(check => new { name = check.Name, text = check.Expression }).ToArray(),
        };
    }

    private object[] ChipRows() =>
        [.. _model.Chips.Select(chip => new
        {
            i = chip.Index,
            kind = chip.Kind.ToString(),
            label = chip.Label,
        })];

    private object[] SearchRows() =>
        [.. _model.SearchIndex.Select(entry => new
        {
            n = entry.NodeIndex,
            field = entry.Field.ToString(),
            text = entry.Text,
        })];
}
