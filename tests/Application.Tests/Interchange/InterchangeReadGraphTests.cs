using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DbClone.Application.Adapters;
using DbClone.Application.Interchange;
using DbClone.Application.Relationship;
using DbClone.Application.Targets;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;

namespace Application.Tests.Interchange;

/// <summary>
/// T1, T7, T15, T18 and T19 over <see cref="IInterchangeService.ReadGraphAsync"/> — the half of the
/// relationship explorer that holds without any HTML: what the graph read counts, what it reads, and
/// what it refuses to invent.
/// </summary>
public class InterchangeReadGraphTests
{
    [Fact]
    public async Task ReadGraphAsync_YieldsOneEdgePerCapturedConstraint_AndAgreesWithItsOwnCounts()
    {
        // Arrange — four captured tables, three constraints, one parent the catalog does not hold.
        using var fixture = CatalogFixtureBuilder.Create()
            .Table(new TestTable("public", "customers")
            {
                Columns = [new("id"), new("name", Nullable: true)],
                PrimaryKey = ["id"],
                RowCount = 3,
            })
            .Table(new TestTable("public", "orders")
            {
                Columns = [new("id"), new("customer_id"), new("shipping_address_id", Nullable: true)],
                PrimaryKey = ["id"],
                ForeignKeys =
                [
                    new("orders_customer_id_fkey", TableRef("public", "customers"),
                        ["customer_id"], ["id"], OnDelete: "NO ACTION", OnUpdate: "NO ACTION"),
                    new("orders_shipping_fkey", TableRef("public", "addresses"),
                        ["shipping_address_id"], ["id"], OnDelete: "SET NULL"),
                ],
                Indexes = [new("orders_customer_id_idx")],
                RowCount = 0,
            })
            .Table(new TestTable("sales", "order_items")
            {
                Columns = [new("order_id"), new("line_no"), new("product_id")],
                PrimaryKey = ["order_id", "line_no"],
                ForeignKeys =
                [
                    new("order_items_order_fkey", TableRef("public", "orders"), ["order_id"], ["id"]),
                ],
                RowCount = 120,
            })
            .Table(new TestTable("public", "audit_log") { Columns = [new("id")] })
            .Build();

        // Act
        var graph = await CreateService().ReadGraphAsync(fixture.Root, CancellationToken.None);

        // Assert — one edge per captured constraint, and the edges agree with the counts the tables
        // themselves state, so a reader is never told two different relation totals.
        graph.Edges.Should().HaveCount(3);
        graph.Tables.Sum(table => table.ForeignKeyCount).Should().Be(graph.Edges.Count);
        graph.Edges.Select(edge => edge.ConstraintName).Should().BeEquivalentTo(
            "orders_customer_id_fkey", "orders_shipping_fkey", "order_items_order_fkey");
        graph.Edges.Select(edge => $"{edge.ChildId}|{edge.ConstraintName}").Should().OnlyHaveUniqueItems();

        // An edge points child → parent: the table that stores the value to the table it comes from.
        var declared = graph.Edges.Single(edge => edge.ConstraintName == "orders_customer_id_fkey");
        declared.ChildId.Should().Be(ObjectRef.Table("public", "orders").Value);
        declared.ParentId.Should().Be(ObjectRef.Table("public", "customers").Value);
        declared.ParentIsCaptured.Should().BeTrue();
        declared.IsSelfReference.Should().BeFalse();
        declared.Columns.Should().Equal("customer_id");
        declared.ReferencedColumns.Should().Equal("id");

        // NO ACTION is not stated; a stated action is carried as written.
        declared.OnDelete.Should().BeNull();
        declared.OnUpdate.Should().BeNull();
        graph.Edges.Single(edge => edge.ConstraintName == "orders_shipping_fkey").OnDelete.Should().Be("SET NULL");

        // Mandatory follows the referencing column's nullability, not the constraint's kind.
        declared.IsMandatory.Should().BeTrue();
        graph.Edges.Single(edge => edge.ConstraintName == "orders_shipping_fkey").IsMandatory.Should().BeFalse();

        graph.Origin.Kind.Should().Be(EGraphOriginKind.Catalog);
        graph.Origin.FormatVersion.Should().Be(4);
        graph.Origin.Dialect.Should().Be("testdialect");
        graph.Origin.Label.Should().Be(Path.GetFileName(fixture.Root));
        graph.Origin.Location.Should().Be(fixture.Root);
        graph.HasData.Should().BeTrue();
        graph.Tables.Should().HaveCount(4);
        graph.UnrelatedTableCount.Should().Be(1, "audit_log declares and receives no constraint");
    }

    [Fact]
    public async Task ReadGraphAsync_StatesRowCountsThatKeepEmptyAndNotCapturedApart()
    {
        // Arrange — a captured empty table, a table with rows, and a table the run captured no data for.
        using var fixture = CatalogFixtureBuilder.Create()
            .Table(new TestTable("public", "empty_one") { Columns = [new("id")], RowCount = 0 })
            .Table(new TestTable("public", "full_one") { Columns = [new("id")], RowCount = 12_345 })
            .Table(new TestTable("public", "schema_only") { Columns = [new("id")] })
            .Build();

        // Act
        var graph = await CreateService().ReadGraphAsync(fixture.Root, CancellationToken.None);

        // Assert — 0 is a measurement and null is its absence; neither may stand for the other.
        RowsOf(graph, "public", "empty_one").Should().Be(0);
        RowsOf(graph, "public", "full_one").Should().Be(12_345);
        RowsOf(graph, "public", "schema_only").Should().BeNull();
        graph.HasData.Should().BeTrue();
    }

    [Fact]
    public async Task ReadGraphAsync_ReportsNoDataForACatalogWhereNoTableStatesARowCount()
    {
        // Arrange
        using var fixture = CatalogFixtureBuilder.Create()
            .Table(new TestTable("public", "schema_only") { Columns = [new("id")] })
            .Build();

        // Act
        var graph = await CreateService().ReadGraphAsync(fixture.Root, CancellationToken.None);

        // Assert
        graph.HasData.Should().BeFalse("the header states no data was captured, once, instead of per node");
        graph.Tables.Should().ContainSingle().Which.RowCount.Should().BeNull();
    }

    [Fact]
    public async Task ReadGraphAsync_MarksTheColumnsThatDeclareAndThatAreReferencedSeparately()
    {
        // Arrange
        using var fixture = CatalogFixtureBuilder.Create()
            .Table(new TestTable("public", "customers")
            {
                Columns = [new("id"), new("note", Nullable: true, Comment: "free text")],
                PrimaryKey = ["id"],
                RowCount = 1,
            })
            .Table(new TestTable("public", "orders")
            {
                Columns = [new("id"), new("customer_id")],
                PrimaryKey = ["id"],
                ForeignKeys = [new("orders_customer_fkey", TableRef("public", "customers"), ["customer_id"], ["id"])],
                RowCount = 1,
            })
            .Build();

        // Act
        var graph = await CreateService().ReadGraphAsync(fixture.Root, CancellationToken.None);

        // Assert — one column declares a constraint, another is its target, and one can be both.
        var customersId = ColumnOf(graph, "public", "customers", "id");
        customersId.IsPartOfPrimaryKey.Should().BeTrue();
        customersId.IsReferencedByForeignKey.Should().BeTrue();
        customersId.IsReferencingForeignKey.Should().BeFalse();

        var ordersCustomerId = ColumnOf(graph, "public", "orders", "customer_id");
        ordersCustomerId.IsReferencingForeignKey.Should().BeTrue();
        ordersCustomerId.IsReferencedByForeignKey.Should().BeFalse();
        ordersCustomerId.IsPartOfPrimaryKey.Should().BeFalse();

        ColumnOf(graph, "public", "customers", "note").Comment.Should().Be("free text");
    }

    [Fact]
    public async Task ReadGraphAsync_StatesEachTablesKeyIndexesAndChecksAsTheCatalogCapturedThem()
    {
        // Arrange — the facts the panel prints so that reading a table never means opening
        // schema/tables.json by hand. An index states its key columns as data; the definition text the
        // producer captured is what a restore replays (D10). One index here states no list, which is how
        // a folder captured before the field existed reads.
        using var fixture = CatalogFixtureBuilder.Create()
            .Table(new TestTable("public", "orders")
            {
                Columns = [new("id"), new("external_ref"), new("total", Nullable: true)],
                PrimaryKey = ["id"],
                Indexes =
                [
                    new("orders_external_ref_idx", IsUnique: true,
                        Definition: "CREATE UNIQUE INDEX orders_external_ref_idx ON public.orders USING btree (external_ref)",
                        Columns: ["external_ref"]),
                    new("orders_total_idx"),
                ],
                UniqueConstraints = [new("orders_external_ref_key", ["external_ref"])],
                CheckConstraints = [new("orders_total_check", "total >= 0")],
                RowCount = 5,
            })
            .Build();

        // Act
        var graph = await CreateService().ReadGraphAsync(fixture.Root, CancellationToken.None);

        // Assert
        var orders = graph.Tables.Should().ContainSingle().Subject;
        orders.PrimaryKey.Should().NotBeNull();
        orders.PrimaryKey!.Name.Should().Be("orders_pkey");
        orders.PrimaryKey.Columns.Should().Equal("id");
        orders.UniqueKeys.Should().ContainSingle();
        orders.UniqueKeys[0].Name.Should().Be("orders_external_ref_key");
        orders.UniqueKeys[0].Columns.Should().Equal("external_ref");
        orders.Indexes.Select(index => index.Name).Should().Equal("orders_external_ref_idx", "orders_total_idx");
        orders.Indexes[0].IsUnique.Should().BeTrue();
        orders.Indexes[0].Columns.Should().Equal("external_ref");
        orders.Indexes[0].Definition.Should().Contain("(external_ref)");
        orders.Indexes[1].Definition.Should().BeNull("a producer that captured no text leaves nothing to invent");
        orders.Indexes[1].Columns.Should().BeNull("no list is not an empty one, so the panel falls back to the text");
        orders.IndexCount.Should().Be(orders.Indexes.Count);
        orders.CheckConstraints.Should().ContainSingle();
        orders.CheckConstraints[0].Name.Should().Be("orders_total_check");
        orders.CheckConstraints[0].Expression.Should().Be("total >= 0");
    }

    [Fact]
    public async Task ReadGraphAsync_StatesNoKeyIndexOrCheckForATableTheCatalogDoesNotHold()
    {
        // Arrange — the parent of a constraint is named but not captured: it gets no table view at all,
        // so nothing may be claimed about its keys, indexes or checks (FR4, T15).
        using var fixture = CatalogFixtureBuilder.Create()
            .Table(new TestTable("public", "orders")
            {
                Columns = [new("id"), new("customer_id")],
                ForeignKeys = [new("orders_customer_fkey", TableRef("public", "customers"), ["customer_id"], ["id"])],
            })
            .Build();

        // Act
        var graph = await CreateService().ReadGraphAsync(fixture.Root, CancellationToken.None);

        // Assert
        graph.Tables.Should().ContainSingle().Which.Table.Should().Be("orders");
        graph.OutsideTables.Should().ContainSingle()
            .Which.Table.Should().Be("customers");
    }

    /// <summary>
    /// T7 — the read opens the schema sections and each table's own manifest, and nothing else. The
    /// two sentinels are the assertion: a relationship section that must never be parsed (it is not
    /// JSON) and a row file whose line count disagrees with the count the table states.
    /// </summary>
    [Fact]
    public async Task ReadGraphAsync_OpensNoRelationshipSectionAndNoRowFile()
    {
        // Arrange
        using var fixture = CatalogFixtureBuilder.Create()
            .Table(new TestTable("public", "orders") { Columns = [new("id")], RowCount = 7 })
            .File("schema/relationships.json", "this section is deliberately not JSON")
            .File("data/public/orders/data.00000.csv", string.Join('\n', Enumerable.Repeat("filler,row", 5_000)))
            .Build();

        // Act
        var graph = await CreateService().ReadGraphAsync(fixture.Root, CancellationToken.None);

        // Assert — the read succeeded, so the relationship section was never parsed, and the row
        // count is the one table.json states rather than the 5 000 lines the chunk holds.
        graph.Tables.Should().ContainSingle().Which.RowCount.Should().Be(7);
    }

    /// <summary>
    /// T15 — an outside table states nothing invented: it carries the name the constraint states and
    /// no field a count, a column list or a definition could be written into.
    /// </summary>
    [Fact]
    public async Task ReadGraphAsync_InventsNothingAboutATableTheCatalogDoesNotHold()
    {
        // Arrange — three constraints onto one parent the catalog does not hold.
        using var fixture = CatalogFixtureBuilder.Create()
            .Table(new TestTable("public", "orders")
            {
                Columns = [new("tenant_id"), new("owner_id"), new("reviewer_id")],
                ForeignKeys =
                [
                    new("orders_tenant_fkey", TableRef("tenant", "accounts"), ["tenant_id"], ["id"]),
                    new("orders_owner_fkey", TableRef("tenant", "accounts"), ["owner_id"], ["id"]),
                    new("orders_reviewer_fkey", TableRef("tenant", "accounts"), ["reviewer_id"], ["id"]),
                ],
                RowCount = 1,
            })
            .Build();

        // Act
        var graph = await CreateService().ReadGraphAsync(fixture.Root, CancellationToken.None);

        // Assert — one node, three separately selectable edges, and no invented metadata.
        graph.OutsideTables.Should().ContainSingle();
        var outside = graph.OutsideTables[0];
        outside.Id.Should().Be(ObjectRef.Table("tenant", "accounts").Value);
        outside.Schema.Should().Be("tenant");
        outside.Table.Should().Be("accounts");
        graph.Edges.Should().HaveCount(3)
            .And.OnlyContain(edge => edge.ParentId == outside.Id && !edge.ParentIsCaptured);
        graph.Tables.Should().NotContain(table => table.Id == outside.Id);
    }

    [Fact]
    public void RelationshipOutsideTableView_HasNoFieldACountOrColumnListCouldBeWrittenInto()
    {
        // Act
        var properties = typeof(RelationshipOutsideTableView)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        // Assert — the type answers "nothing is known" by construction, so the renderer cannot print
        // a 0, a dash or a guess for a table the folder never described.
        properties.Should().Equal("Id", "Schema", "Table");
    }

    /// <summary>
    /// T18 — identity is separate from label: two references that state no name stay two nodes, a
    /// reference that states a name but no schema keeps that name and invents no schema, and two
    /// identical references converge on one node with both edges kept.
    /// </summary>
    [Fact]
    public async Task ReadGraphAsync_KeysReferencesByIdentityAndNeverByRenderedText()
    {
        // Arrange — two nameless targets, one name without a schema, and one shared full reference.
        using var fixture = CatalogFixtureBuilder.Create()
            .Table(new TestTable("public", "first")
            {
                Columns = [new("a"), new("b"), new("c"), new("d")],
                ForeignKeys =
                [
                    new("fk_alpha", string.Empty, ["a"], []),
                    new("fk_beta", string.Empty, ["b"], []),
                    new("fk_nameless_schema", "table:orders", ["c"], ["id"]),
                    new("fk_shared", TableRef("tenant", "accounts"), ["d"], ["id"]),
                ],
                RowCount = 1,
            })
            .Table(new TestTable("public", "second")
            {
                Columns = [new("e")],
                ForeignKeys = [new("fk_same_target", TableRef("tenant", "accounts"), ["e"], ["id"])],
                RowCount = 1,
            })
            .Build();

        // Act
        var graph = await CreateService().ReadGraphAsync(fixture.Root, CancellationToken.None);

        // Assert — two unnamed references are two nodes, not one: a key derived from the words a
        // reader sees would have folded them together.
        var unnamed = graph.OutsideTables.Where(table => table.Schema is null && table.Table is null).ToList();
        unnamed.Should().HaveCount(2);
        unnamed.Select(table => table.Id).Should().OnlyHaveUniqueItems();

        graph.Edges.Where(edge => unnamed.Any(table => table.Id == edge.ParentId))
            .Select(edge => edge.ConstraintName)
            .Should().BeEquivalentTo("fk_alpha", "fk_beta");

        // A name but no schema keeps the name and invents no schema.
        var nameOnly = graph.OutsideTables.Single(table => table.Id == "table:orders");
        nameOnly.Schema.Should().BeNull();
        nameOnly.Table.Should().Be("orders");

        // Two identical references converge on one node, and both edges survive.
        graph.OutsideTables.Where(table => table.Id == ObjectRef.Table("tenant", "accounts").Value)
            .Should().ContainSingle();
        graph.Edges.Count(edge => edge.ParentId == ObjectRef.Table("tenant", "accounts").Value).Should().Be(2);

        graph.OutsideTables.Should().HaveCount(4);
    }

    /// <summary>
    /// T19 — a data folder the index names but the folder does not hold is the product's own
    /// contradiction, surfaced as an error rather than drawn around (FR10).
    /// </summary>
    [Fact]
    public async Task ReadGraphAsync_SurfacesACatalogThatNamesADataFolderItDoesNotHold()
    {
        // Arrange
        using var fixture = CatalogFixtureBuilder.Create()
            .Table(new TestTable("public", "orders") { Columns = [new("id")] })
            .MissingDataFolder("public", "orders")
            .Build();

        // Act
        var act = () => CreateService().ReadGraphAsync(fixture.Root, CancellationToken.None);

        // Assert
        var thrown = await act.Should().ThrowAsync<Exception>();
        thrown.Which.Message.Should().Contain("records the data folder").And.Contain("no such folder");
    }

    /// <summary>
    /// T19 — an index that disagrees with the tables section is refused with both counts, never
    /// drawn as the smaller set.
    /// </summary>
    [Fact]
    public async Task ReadGraphAsync_RefusesAnIndexThatDisagreesWithTheTablesSection()
    {
        // Arrange
        using var fixture = CatalogFixtureBuilder.Create()
            .Table(new TestTable("public", "orders") { Columns = [new("id")] })
            .Table(new TestTable("public", "items") { Columns = [new("id")] })
            .DeclaredTableCount(3)
            .Build();

        // Act
        var act = () => CreateService().ReadGraphAsync(fixture.Root, CancellationToken.None);

        // Assert
        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("records 3 table(s)").And.Contain("holds 2");
    }

    /// <summary>
    /// T19 — a constraint naming a table the catalog does not hold is not an error: the table is
    /// drawn outside the catalog, and it joins no schema row, because the catalog never confirmed
    /// that schema.
    /// </summary>
    [Fact]
    public async Task ReadGraphAsync_DrawsAPartialStoreWithoutAttributingTheOutsideTableToTheSchema()
    {
        // Arrange — the schema is captured, the table is not.
        using var fixture = CatalogFixtureBuilder.Create()
            .Table(new TestTable("public", "orders")
            {
                Columns = [new("address_id")],
                ForeignKeys = [new("orders_address_fkey", TableRef("public", "addresses"), ["address_id"], ["id"])],
                RowCount = 1,
            })
            .Build();

        // Act
        var graph = await CreateService().ReadGraphAsync(fixture.Root, CancellationToken.None);

        // Assert
        graph.OutsideTables.Should().ContainSingle()
            .Which.Schema.Should().Be("public");
        graph.Schemas.Should().ContainSingle()
            .Which.Should().Be(new RelationshipSchemaView(Schema: "public", TableCount: 1, RelatedTableCount: 1));
        graph.UnrelatedTableCount.Should().Be(0);
    }

    [Fact]
    public async Task ReadGraphAsync_RejectsAMissingCatalogFolder()
    {
        // Arrange
        var missing = Path.Combine(Path.GetTempPath(), "dbclone-missing-" + Guid.NewGuid().ToString("N"));

        // Act
        var act = () => CreateService().ReadGraphAsync(missing, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    private static InterchangeService CreateService()
    {
        var adapter = Substitute.For<IDatabaseAdapter>();
        adapter.Info.Returns(new AdapterInfo("test.postgresql", "postgresql", "*", "1.0.0"));
        adapter.Syntax.Returns(Substitute.For<IConnectionStringSyntax>());

        var catalog = Substitute.For<IAdapterCatalog>();
        catalog.All.Returns([adapter]);
        catalog.Find(Arg.Any<string>()).Returns(adapter);

        return new InterchangeService(
            new DatabaseTargetFactory(catalog),
            Substitute.For<ILogger<InterchangeService>>());
    }

    private static string TableRef(string schema, string name) => ObjectRef.Table(schema, name).Value;

    private static long? RowsOf(RelationshipGraphView graph, string schema, string table) =>
        graph.Tables
            .Single(t => t.Schema == schema && t.Table == table)
            .RowCount;

    private static RelationshipColumnView ColumnOf(RelationshipGraphView graph, string schema, string table, string column) =>
        graph.Tables
            .Single(t => t.Schema == schema && t.Table == table)
            .Columns
            .Single(c => c.Name == column);
}
