using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.PostgreSql.DependencyAnalysis;

using FluentAssertions;

namespace PostgreSql.Tests;

public class DependencyOrderingTests
{
    [Fact]
    public void Sort_DistinguishesObjectTypes()
    {
        // Same qualified name under different object types must not be confused.
        var result = new DependencyResult(
                [
                    Obj(EDatabaseObjectType.MaterializedView, "public", "thing"),
                    Obj(EDatabaseObjectType.View, "public", "thing")
                ],
                []);

        var sorted = DependencyOrdering.Sort(
            new[] { "view-item", "mv-item" },
            result,
            k => k == "mv-item"
                     ? (EDatabaseObjectType.MaterializedView, "public.thing")
                     : (EDatabaseObjectType.View, "public.thing"));

        sorted.Should().ContainInOrder("mv-item", "view-item");
    }

    [Fact]
    public void Sort_NullResult_KeepsOriginalOrder()
    {
        var sorted = DependencyOrdering.Sort(
            new[] { "public.a", "public.b" },
            null,
            k => (EDatabaseObjectType.View, k));

        sorted.Should().ContainInOrder("public.a", "public.b");
    }

    [Fact]
    public void Sort_OrdersItemsByDependencyResult()
    {
        var result = new DependencyResult(
                [
                    Obj(EDatabaseObjectType.View, "public", "v2"),
                    Obj(EDatabaseObjectType.View, "public", "v1")
                ],
                []);

        var sorted = DependencyOrdering.Sort(
            new[] { "public.v1", "public.v2" },
            result,
            k => (EDatabaseObjectType.View, k));

        sorted.Should().ContainInOrder("public.v2", "public.v1");
    }

    [Fact]
    public void Sort_UnknownItemsGoLast_InStableOriginalOrder()
    {
        var result = new DependencyResult([Obj(EDatabaseObjectType.View, "public", "v2")], []);

        var sorted = DependencyOrdering.Sort(
            new[] { "public.x", "public.v2", "public.y" },
            result,
            k => (EDatabaseObjectType.View, k));

        sorted.Should().ContainInOrder("public.v2", "public.x", "public.y");
    }

    private static DatabaseObject Obj(EDatabaseObjectType type, string schema, string name) =>
        new(SchemaName: schema, Name: name, ObjectType: type, Dependencies: []);
}
