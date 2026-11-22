using System.Globalization;
using DbClone.Application.Relationship;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services.GraphAnalysis;
using DbClone.UI.Services.GraphLayout;

namespace UI.Tests.CatalogGraph;

/// <summary>Stable fake catalogs shared by current and reconstructed pre-refactor characterization.</summary>
internal static class GraphQualityFixtures
{
    public static readonly string[] Names =
        ["unobstructed", "lightly-obstructed", "dense", "chain", "hub", "components", "cycle", "scale"];

    /// <summary>
    /// The shape of a catalog a person actually keeps: 79 tables, 96 relations, one table with 27 ends,
    /// eight self-references and seventeen tables with no relation at all. It is kept out of <see cref="Names"/>
    /// because it is not one of the measured fixtures; it is the shape a report was made against.
    /// </summary>
    public const string Catalog = "catalog";

    /// <summary>
    /// Two settlements of five tables joined among themselves and two tables that reach into both: the smallest
    /// shape that has both the clusters a ring must keep apart and the cross-relations that make routing hard.
    /// It is the quick gate of <see cref="GraphLayoutIntegrationTests"/> and not a measured fixture.
    /// </summary>
    public const string Plaza = "plaza";

    /// <summary>
    /// Two catalog-scale settlements of thirty-nine tables each, joined to one another by three relations, with
    /// one table no relation reaches: neither settlement is a hub, so the arrangement this shape calls for is
    /// two groups held apart. The counts are pinned by <c>GraphQualityFixturesTests</c>.
    /// </summary>
    public const string Clusters = "clusters";

    /// <summary>
    /// A catalog-scale ring of seventy-eight tables joined around it, one chord across it, and one table no
    /// relation reaches: the arrangement this shape calls for is the ring. The counts are pinned by
    /// <c>GraphQualityFixturesTests</c>.
    /// </summary>
    public const string Ring = "ring";

    public static CatalogGraphModel Model(string name) => new CatalogGraphAnalyzer().Analyze(View(name));

    /// <summary>The graph a named fixture states, as the reader hands it over, before it is analyzed.</summary>
    /// <param name="name">The fixture's name.</param>
    /// <returns>The view the document pipeline starts from.</returns>
    public static RelationshipGraphView View(string name)
    {
        var fixture = CatalogGraphFixture.Create();
        for (var node = 0; node < Count(name); node++)
        {
            fixture.Table("test_schema", TableName(node), 0);
        }

        var edges = Edges(name);
        for (var edge = 0; edge < edges.Length; edge++)
        {
            var (child, parent) = edges[edge];
            fixture.Edge(Id(child), Id(parent), "test_fk" + edge.ToString("D4", CultureInfo.InvariantCulture), ["test_parent_id"]);
        }

        return fixture.Build();
    }

    /// <summary>
    /// The layout facts of a named fixture, sized exactly as the engine sizes them: the text sets a
    /// card's width and the arrow ends that meet it set a floor under that.
    /// </summary>
    /// <param name="name">The fixture's name.</param>
    /// <returns>The graph facts the selector works on.</returns>
    public static GraphLayoutProfile Profile(string name) => Sized(Model(name));

    /// <summary>
    /// The layout facts of an arbitrary shape, sized exactly as the engine sizes them, built from a bare list
    /// of relations instead of a named fixture. This is what lets a test ask about a catalog nobody wrote down.
    /// </summary>
    /// <param name="count">How many tables the shape has.</param>
    /// <param name="edges">The relations as (child, parent) pairs.</param>
    /// <returns>The graph facts the selector works on.</returns>
    public static GraphLayoutProfile Profile(int count, IReadOnlyList<(int Child, int Parent)> edges)
    {
        var fixture = CatalogGraphFixture.Create();
        for (var node = 0; node < count; node++)
        {
            fixture.Table("test_schema", TableName(node), 0);
        }

        for (var edge = 0; edge < edges.Count; edge++)
        {
            var (child, parent) = edges[edge];
            fixture.Edge(Id(child), Id(parent), "test_fk" + edge.ToString("D4", CultureInfo.InvariantCulture), ["test_parent_id"]);
        }

        return Sized(new CatalogGraphAnalyzer().Analyze(fixture.Build()));
    }

    private static GraphLayoutProfile Sized(CatalogGraphModel model)
    {
        var ends = new int[model.Nodes.Count];
        foreach (var edge in model.Edges)
        {
            ends[edge.ChildIndex]++;
            ends[edge.ParentIndex]++;
        }

        var words = GraphOriginWords.For(model.Origin.Kind);
        var widths = model.Nodes
            .Select((node, index) => Math.Max(GraphCard.WidthOf(node, words), GraphPortAssigner.MinimumWidth(ends[index], GraphCard.Height)))
            .ToArray();

        return GraphLayoutProfile.From(model, widths);
    }

    private static string TableName(int node) => "test_table" + node.ToString("D4", CultureInfo.InvariantCulture);

    private static string Id(int node) => CatalogGraphFixture.TableId("test_schema", TableName(node));

    private static int Count(string name) => name switch
    {
        "unobstructed" => 2,
        "lightly-obstructed" => 7,
        "dense" => 6,
        "chain" => 12,
        "hub" => 15,
        "components" => 9,
        "cycle" => 8,
        "catalog" => 79,
        "plaza" => 12,
        "clusters" => 79,
        "ring" => 79,
        "scale" => 1000,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static (int Child, int Parent)[] Edges(string name) => name switch
    {
        "unobstructed" => [(1, 0)],
        "lightly-obstructed" => [.. Enumerable.Range(1, 6).Select(node => (node, 0)), (6, 1)],
        "dense" => [.. Enumerable.Range(0, 6).Select(node => (node, (node + 1) % 6)), (0, 3), (1, 4), (2, 5)],
        "chain" => Enumerable.Range(1, 11).Select(node => (node, node - 1)).ToArray(),
        "hub" => Enumerable.Range(1, 14).Select(node => (node, 0)).ToArray(),
        "components" => [(1, 0), (2, 0), (4, 3), (5, 3), (7, 6), (8, 6), (8, 7)],
        "cycle" => [(0, 1), (1, 2), (2, 3), (3, 0), (4, 0), (5, 4), (6, 5), (7, 7), (7, 6), (2, 6)],
        "catalog" =>
        [
            (3, 24), (5, 21), (6, 7), (7, 24), (8, 7), (8, 24), (9, 8), (10, 12), (10, 24), (13, 12),
            (13, 24), (14, 24), (15, 21), (16, 23), (17, 2), (17, 23), (19, 23), (20, 23), (20, 24), (21, 12),
            (21, 24), (22, 23), (25, 24), (26, 24), (27, 24), (27, 27), (27, 51), (28, 24), (28, 24), (28, 45),
            (29, 24), (29, 45), (30, 28), (30, 58), (30, 51), (31, 28), (31, 32), (31, 32), (32, 32), (32, 28),
            (33, 28), (34, 34), (34, 28), (35, 29), (38, 24), (38, 45), (38, 24), (39, 40), (39, 45), (40, 40),
            (40, 45), (41, 45), (42, 42), (42, 45), (43, 44), (43, 45), (43, 29), (44, 44), (44, 45), (45, 24),
            (46, 51), (46, 24), (47, 24), (47, 37), (47, 51), (47, 51), (50, 24), (50, 51), (51, 24), (51, 36),
            (51, 24), (51, 51), (51, 45), (51, 48), (51, 49), (53, 29), (53, 53), (53, 29), (53, 29), (54, 29),
            (54, 29), (55, 29), (56, 24), (56, 29), (57, 51), (57, 24), (58, 51), (59, 24), (59, 52), (59, 24),
            (60, 24), (75, 71), (76, 71), (77, 71), (77, 76), (78, 73),
        ],
        "scale" => Enumerable.Range(1, 999).Select(node => (node, node - 1)).ToArray(),
        "plaza" =>
        [
            .. Enumerable.Range(0, 2).SelectMany(cluster => Enumerable.Range(0, 4).Select(node => ((cluster * 5) + node + 1, (cluster * 5) + node))),
            .. Enumerable.Range(0, 2).Select(cluster => ((cluster * 5) + 4, cluster * 5)),
            .. Enumerable.Range(0, 2).SelectMany(hub => Enumerable.Range(0, 2).Select(target => (10 + hub, (target * 5) + hub))),
            .. Enumerable.Range(0, 2).Select(hub => (10 + hub, 10 + ((hub + 1) % 2))),
        ],
        "clusters" =>
        [
            .. Enumerable.Range(1, 29).Select(node => (node, 0)),
            .. Enumerable.Range(31, 8).Select(node => (node, node - 1)),
            (30, 0),
            .. Enumerable.Range(40, 29).Select(node => (node, 39)),
            .. Enumerable.Range(70, 8).Select(node => (node, node - 1)),
            (69, 39),
            (39, 0),
            (10, 39),
            (20, 40),
        ],
        "ring" =>
        [
            .. Enumerable.Range(1, 77).Select(node => (node, node - 1)),
            (0, 77),
            (0, 39),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
