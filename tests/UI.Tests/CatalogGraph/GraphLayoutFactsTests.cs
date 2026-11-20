using DbClone.Application.Relationship;
using DbClone.UI.Services.GraphAnalysis;
using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// Locks the account a picture gives of itself: which arrangement was chosen, what its routes cost, and which
/// table the relations lean on. That account is how the next arrangement is chosen, so it has to be stated by
/// the search that made the choice rather than reconstructed from the picture afterwards.
/// </summary>
public sealed class GraphLayoutFactsTests
{
    [Fact]
    public void The_picture_states_the_arrangement_it_chose_its_cost_and_the_table_most_relations_meet()
    {
        // Arrange: two relations, one of which leans on public.orders from both sides of the catalog.
        var view = CatalogGraphFixture.Create()
            .Table("public", "orders", 10, Column("id"))
            .Table("public", "customers", 10, Column("id"))
            .Table("public", "order_lines", 10, Column("order_id"))
            .Edge(
                CatalogGraphFixture.TableId("public", "orders"),
                CatalogGraphFixture.TableId("public", "customers"),
                "fk_orders_customers",
                ["customer_id"],
                ["id"])
            .Edge(
                CatalogGraphFixture.TableId("public", "order_lines"),
                CatalogGraphFixture.TableId("public", "orders"),
                "fk_lines_orders",
                ["order_id"],
                ["id"])
            .Build();
        var model = new CatalogGraphAnalyzer().Analyze(view);

        // Act
        var result = new GraphLayoutEngine().Compute(model);

        // Assert
        result.Facts.Should().NotBeNull("a picture is judged by the search's own account of it");
        var facts = result.Facts!;
        facts.Arrangement.Should().BeOneOf(
            ["ranked", "radial", "force", "force-wide", "force-transposed", "assembled"],
            "the chosen arrangement is one of the seeds the search generates");
        facts.Tables.Should().Be(3);
        facts.Relations.Should().Be(2);
        facts.Crossings.Should().Be(0, "two relations that share a table are drawn apart");
        facts.Busiest.Should().Be("public.orders", "both relations meet it and each of the others meets one");
        facts.BusiestRelations.Should().Be(2);
        facts.Width.Should().BeGreaterThan(0);
        facts.Height.Should().BeGreaterThan(0);
    }

    [Fact]
    public void The_account_names_every_seed_and_what_it_did()
    {
        // A fan wider than a page is the case whose seeds disagree, so the account has to speak about each.
        var fixture = CatalogGraphFixture.Create().Table("fan", "parent", 7);
        for (var child = 0; child < 9; child++)
        {
            fixture.Table("fan", $"child{child:00}", 3, Column("parent_id"));
            fixture.Edge(
                CatalogGraphFixture.TableId("fan", $"child{child:00}"),
                CatalogGraphFixture.TableId("fan", "parent"),
                $"fk_child_{child:00}",
                ["parent_id"]);
        }

        var model = new CatalogGraphAnalyzer().Analyze(fixture.Build());

        var facts = new GraphLayoutEngine().Compute(model).Facts!;

        facts.Seeds.Should().Contain("ranked").And.Contain("radial").And.Contain("force");
        facts.Seeds.Should().MatchRegex(
            @"(ranked|radial|force|force-wide|force-transposed) (\d+/\d+|refused)",
            "a seed is named by the cost it reached or by the failure that refused it");
    }

    private static RelationshipColumnView Column(string name) =>
        new(name, "integer", false, true, false, false, null);
}
