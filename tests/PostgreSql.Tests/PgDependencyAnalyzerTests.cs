using DbClone.Application.Enums;
using DbClone.Application.Models;
using DbClone.PostgreSql.DependencyAnalysis;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

namespace PostgreSql.Tests;

public class PgDependencyAnalyzerTests
{
    private readonly PgDependencyAnalyzer
        _analyzer = new(NullLogger<PgDependencyAnalyzer>.Instance);

    [Theory]
    [InlineData("inner_type")] // bare reference
    [InlineData("public.inner_type")] // qualified reference
    [InlineData("inner_type[]")] // array of the referenced type
    public async Task AnalyzeAsync_CompositeReferenceForms_OrdersDependencyFirst(
        string attributeType)
    {
        var model = CreateModel(
            schemas: [new SchemaDefinition("public", "postgres")],
            compositeTypes:
                [
                    CreateCompositeType("public", "wrapper", attributeType),
                    CreateCompositeType("public", "inner_type", "integer")
                ]);

        var result = await _analyzer.AnalyzeAsync(model);

        var names = result.OrderedObjects
            .Where(o => o.ObjectType == EDatabaseObjectType.CompositeType)
            .Select(o => o.Name)
            .ToList();
        names.IndexOf("inner_type").Should().BeLessThan(names.IndexOf("wrapper"));
    }

    [Fact]
    public async Task AnalyzeAsync_CompositeReferencingComposite_OrdersDependencyFirst()
    {
        // "wrapper" is listed first but references "inner_type" — the dependency
        // must be ordered first so CreateTypesStage can create both in one pass.
        var model = CreateModel(
            schemas: [new SchemaDefinition("public", "postgres")],
            compositeTypes:
                [
                    CreateCompositeType("public", "wrapper", attributeType: "inner_type"),
                    CreateCompositeType("public", "inner_type", attributeType: "integer")
                ]);

        var result = await _analyzer.AnalyzeAsync(model);

        var names = result.OrderedObjects
            .Where(o => o.ObjectType == EDatabaseObjectType.CompositeType)
            .Select(o => o.Name)
            .ToList();
        names.IndexOf("inner_type").Should().BeLessThan(names.IndexOf("wrapper"));
        result.CircularDependencies.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_CompositeReferencingEnum_OrdersEnumFirst()
    {
        var model = CreateModel(
            schemas: [new SchemaDefinition("public", "postgres")],
            enums: [new EnumDefinition("public", "status", ["active", "inactive"], null)],
            compositeTypes: [CreateCompositeType("public", "wrapper", attributeType: "status")]);

        var result = await _analyzer.AnalyzeAsync(model);

        var enumIdx = result.OrderedObjects
            .ToList().FindIndex(o =>
                o.ObjectType == EDatabaseObjectType.Enum && o.Name == "status");
        var compositeIdx = result.OrderedObjects
            .ToList().FindIndex(o =>
                o.ObjectType == EDatabaseObjectType.CompositeType && o.Name == "wrapper");
        enumIdx.Should().BeLessThan(compositeIdx);
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyModel_ReturnsEmpty()
    {
        var model = CreateEmptyModel();

        var result = await _analyzer.AnalyzeAsync(model);

        result.OrderedObjects.Should().BeEmpty();
        result.CircularDependencies.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_SingleTable_ReturnsOrdered()
    {
        var model = CreateModel(
            schemas: [new SchemaDefinition("public", "postgres")],
            tables: [CreateTable("public", "users", foreignKeys: [])]);

        var result = await _analyzer.AnalyzeAsync(model);

        result.OrderedObjects.Should().HaveCountGreaterThan(0);
        result.CircularDependencies.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_TablesWithFk_SortsCorrectly()
    {
        var model = CreateModel(
            schemas: [new SchemaDefinition("public", "postgres")],
            tables:
                [
                    CreateTable(
                        "public",
                        "orders",
                        foreignKeys:
                            [
                                new ForeignKeyDefinition(
                                    "fk_order_user",
                                        ["user_id"],
                                    "public",
                                    "users",
                                        ["id"],
                                    "NO ACTION",
                                    "NO ACTION",
                                    false,
                                    false)
                            ]),
                    CreateTable("public", "users", foreignKeys: [])
                ]);

        var result = await _analyzer.AnalyzeAsync(model);

        var tableNames = result.OrderedObjects
            .Where(o => o.ObjectType == EDatabaseObjectType.Table)
            .Select(o => o.Name)
            .ToList();

        // "users" should come before "orders"
        var usersIdx = tableNames.IndexOf("users");
        var ordersIdx = tableNames.IndexOf("orders");
        usersIdx.Should().BeLessThan(ordersIdx);
    }

    [Fact]
    public async Task AnalyzeAsync_ViewCycle_ReportedAsCircularDependency()
    {
        var model = CreateModel(
            schemas: [new SchemaDefinition("public", "postgres")],
            views:
                [
                    new ViewDefinition(
                        "public",
                        "v1",
                        " SELECT * FROM v2",
                        null,
                        ReferencedRelations: ["public.v2"]),
                    new ViewDefinition(
                        "public",
                        "v2",
                        " SELECT * FROM v1",
                        null,
                        ReferencedRelations: ["public.v1"])
                ]);

        var result = await _analyzer.AnalyzeAsync(model);

        result.CircularDependencies.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_ViewReferencingView_OrdersDependencyFirst()
    {
        // v1 selects from v2 but is listed first.
        var model = CreateModel(
            schemas: [new SchemaDefinition("public", "postgres")],
            views:
                [
                    new ViewDefinition(
                        "public",
                        "v1",
                        " SELECT * FROM v2",
                        null,
                        ReferencedRelations: ["public.v2"]),
                    new ViewDefinition("public", "v2", " SELECT 1", null, ReferencedRelations: [])
                ]);

        var result = await _analyzer.AnalyzeAsync(model);

        var names = result.OrderedObjects
            .Where(o => o.ObjectType == EDatabaseObjectType.View)
            .Select(o => o.Name)
            .ToList();
        names.IndexOf("v2").Should().BeLessThan(names.IndexOf("v1"));
        result.CircularDependencies.Should().BeEmpty();
    }

    [Theory]
    [InlineData("my_type", "my_type")]
    [InlineData("my_type[]", "my_type")]
    [InlineData("my_type[][]", "my_type")]
    [InlineData("public.my_type", "public.my_type")]
    [InlineData("character varying(50)", "character varying")]
    [InlineData("numeric(10,2)", "numeric")]
    [InlineData("\"MyType\"", "MyType")]
    [InlineData(" integer ", "integer")]
    public void NormalizeTypeName_StripsArraysModifiersAndQuotes(string input, string expected)
    {
        PgDependencyAnalyzer.NormalizeTypeName(input).Should().Be(expected);
    }

    [Fact]
    public void TryResolveUserType_BuiltinType_DoesNotResolve()
    {
        var qualified =
            new Dictionary<string, EDatabaseObjectType>(StringComparer.OrdinalIgnoreCase);
        var bare = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        PgDependencyAnalyzer.TryResolveUserType("integer", "public", qualified, bare, out _)
            .Should().BeFalse();
        PgDependencyAnalyzer.TryResolveUserType("text[]", "public", qualified, bare, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryResolveUserType_UnqualifiedName_PrefersOwnerSchema()
    {
        var qualified =
            new Dictionary<string, EDatabaseObjectType>(StringComparer.OrdinalIgnoreCase)
                {
                    ["a.status"] = EDatabaseObjectType.Enum, ["b.status"] = EDatabaseObjectType.Enum
                };
        var bare = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                       {
                           ["status"] = ["a.status", "b.status"]
                       };

        PgDependencyAnalyzer.TryResolveUserType("status", "b", qualified, bare, out var reference)
            .Should().BeTrue();
        reference.SchemaName.Should().Be("b");
        reference.Name.Should().Be("status");
    }

    private static CompositeTypeDefinition CreateCompositeType(
        string schema,
        string name,
        string attributeType) =>
        new(
            schema,
            name,
                [
                    new ColumnDefinition(
                        "attr",
                        attributeType,
                        1,
                        true,
                        null,
                        false,
                        false,
                        null,
                        null)
                ],
            null);

    private static DatabaseModel CreateEmptyModel() =>
        new("test", "16.0", [], [], [], [], [], [], [], [], [], [], [], [], [], []);

    private static DatabaseModel CreateModel(
        IReadOnlyList<SchemaDefinition>? schemas = null,
        IReadOnlyList<TableDefinition>? tables = null,
        IReadOnlyList<ViewDefinition>? views = null,
        IReadOnlyList<EnumDefinition>? enums = null,
        IReadOnlyList<CompositeTypeDefinition>? compositeTypes = null) =>
        new(
            "test",
            "16.0",
            schemas ?? [],
            tables ?? [],
            views ?? [],
                [],
                [],
            enums ?? [],
                [],
            compositeTypes ?? [],
                [],
                [],
                [],
                [],
                [],
                []);

    private static TableDefinition CreateTable(
        string schema,
        string name,
        IReadOnlyList<ForeignKeyDefinition> foreignKeys) =>
        new(schema, name, [], [], foreignKeys, [], [], null, false, null, null);
}
