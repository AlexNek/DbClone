using CanonicalModel.Enums;
using CanonicalModel.Interchange;

using DbAdapter.PostgreSql.Interchange;

using FluentAssertions;

using Models = CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// The identity layer the mapper resolves its references through. Identity is computed from the
/// object's place, never from an engine oid, so the same database state must produce the same ids
/// on every run — including where PostgreSQL alone makes that hard: overloads, objects the model
/// does not list, and dependencies on relations the catalog does not contain.
/// </summary>
public class InterchangeIdGraphTests
{
    private const string Dialect = "postgresql";

    [Fact]
    public void Schema_MatchesTheComputedIdentityOfItsName()
    {
        var graph = InterchangeIdGraph.Create(InterchangeTestModel.Full(), Dialect);
        var expected = InterchangeId.Compute("schema", string.Empty, "public");

        graph.Schema("public").Should().Be(expected);
    }

    [Fact]
    public void Schema_GivesAnUnlistedSchemaAStableIdInsteadOfFailing()
    {
        var graph = InterchangeTestModel.Full();
        var first = InterchangeIdGraph.Create(graph, Dialect).Schema("extensions");
        var second = InterchangeIdGraph.Create(graph, Dialect).Schema("extensions");

        // A schema created by an extension is not always in model.Schemas; it still needs one id.
        first.Should().Be(second);
        first.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Table_IsParentedOnItsSchemaId()
    {
        var graph = InterchangeIdGraph.Create(InterchangeTestModel.Full(), Dialect);

        graph.Table("public", "customers")
            .Should()
            .Be(InterchangeId.Compute(
                "table",
                "public",
                "customers",
                graph.Schema("public")));
    }

    [Fact]
    public void Table_DistinguishesTablesThatShareANameAcrossSchemas()
    {
        var graph = InterchangeIdGraph.Create(InterchangeTestModel.Full(), Dialect);

        graph.Table("public", "orders").Should().NotBe(graph.Table("reporting", "orders"));
    }

    [Fact]
    public void Column_DistinguishesColumnsByTheirTable()
    {
        var graph = InterchangeIdGraph.Create(InterchangeTestModel.Full(), Dialect);
        var customersId = graph.Table("public", "customers");
        var ordersId = graph.Table("public", "orders");

        graph.Column("public", "customers", "id", customersId)
            .Should()
            .NotBe(graph.Column("public", "orders", "id", ordersId));
    }

    [Fact]
    public void Routine_KnowsEachOverloadApartAndResolvesTheNameStably()
    {
        var model = Overloaded();
        var graph = InterchangeIdGraph.Create(model, Dialect);

        graph.Routine("public", "roll", "integer").Should().NotBe(graph.Routine("public", "roll", "text"));

        // A trigger names its function without its argument types. The reference must still be
        // reproducible, so the graph picks one overload in sorted order rather than the first one
        // the catalog query happened to return.
        var byName = graph.Routine("public", "roll", null);
        InterchangeIdGraph.Create(model, Dialect).Routine("public", "roll", null).Should().Be(byName);
        byName.Should().BeOneOf(
            graph.Routine("public", "roll", "integer"),
            graph.Routine("public", "roll", "text"));
    }

    [Fact]
    public void Routine_FallsBackToTheSignaturelessIdWhenTheNameIsUnknown()
    {
        var graph = InterchangeIdGraph.Create(InterchangeTestModel.Full(), Dialect);

        graph.Routine("public", "not_captured", null)
            .Should()
            .Be(InterchangeId.Compute("routine", "public", "not_captured()", graph.Schema("public")));
    }

    [Fact]
    public void ResolveRelations_MatchesCapturedObjectsAndSkipsTheRest()
    {
        var graph = InterchangeIdGraph.Create(InterchangeTestModel.Full(), Dialect);

        var resolved = graph.ResolveRelations(
            ["public.ghost", "public.customers", "public.customers", "reporting.mv_totals"]);

        resolved.Should().Equal(
            graph.Table("public", "customers"),
            graph.View("reporting", "mv_totals"));
    }

    [Fact]
    public void ResolveRelations_KnowsViewsAndTablesShareOneNameSpace()
    {
        var graph = InterchangeIdGraph.Create(InterchangeTestModel.Full(), Dialect);

        graph.ResolveRelations(["reporting.key_value"]).Should().Equal(graph.Table("reporting", "key_value"));
    }

    private static Models.DatabaseModel Overloaded() =>
        InterchangeTestModel.Empty() with
        {
            Functions =
            [
                // Listed deliberately in the order a catalog query might not repeat.
                new Models.FunctionDefinition(
                    "public", "roll", "plpgsql", "integer", "CREATE FUNCTION ...", [],
                    EFunctionVolatility.Volatile, false, false, null, false),
                new Models.FunctionDefinition(
                    "public", "roll", "plpgsql", "integer", "CREATE FUNCTION ...",
                    [new Models.FunctionParameter("v", "text", EParameterMode.In, null)],
                    EFunctionVolatility.Volatile, false, false, null, false),
                new Models.FunctionDefinition(
                    "public", "roll", "plpgsql", "integer", "CREATE FUNCTION ...",
                    [new Models.FunctionParameter("v", "integer", EParameterMode.In, null)],
                    EFunctionVolatility.Volatile, false, false, null, false),
            ],
        };
}
