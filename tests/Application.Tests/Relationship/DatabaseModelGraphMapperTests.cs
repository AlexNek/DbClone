using DbClone.Application.Relationship;

using FluentAssertions;

namespace Application.Tests.Relationship;

/// <summary>
/// The whole conversion a live read performs: one edge per foreign key in the constraint's own column
/// order, keys and indexes carried from the model, node identities assigned rather than concatenated,
/// and a reference the model cannot resolve stated as its own node.
/// </summary>
public class DatabaseModelGraphMapperTests
{
    private static readonly GraphViewOrigin Origin = new(
        EGraphOriginKind.LiveDatabase,
        "testdb",
        "test.example.com:5432/testdb",
        "postgresql",
        FormatVersion: null);

    [Fact]
    public void Map_YieldsOneEdgePerForeignKey_AndStatesTheModelAsTheOrigin()
    {
        // Arrange — two captured tables, two constraints, one parent the database does not hold.
        var model = RelationshipModelFixture.Model(
            RelationshipModelFixture.Table(
                "public",
                "customers",
                columns: [RelationshipModelFixture.Column("id", 1)],
                indexes: [RelationshipModelFixture.PrimaryIndex("id")]),
            RelationshipModelFixture.Table(
                "public",
                "orders",
                columns:
                [
                    RelationshipModelFixture.Column("id", 1),
                    RelationshipModelFixture.Column("customer_id", 2),
                    RelationshipModelFixture.Column("shipping_address_id", 3, nullable: true),
                ],
                indexes: [RelationshipModelFixture.PrimaryIndex("id")],
                foreignKeys:
                [
                    RelationshipModelFixture.ForeignKey(
                        "orders_customer_id_fkey", "public", "customers", ["customer_id"], ["id"]),
                    RelationshipModelFixture.ForeignKey(
                        "orders_shipping_fkey", "public", "addresses", ["shipping_address_id"], ["id"], deleteRule: "SET NULL"),
                ]));

        // Act
        var view = DatabaseModelGraphMapper.Map(model, Origin);

        // Assert — one edge per constraint, named as the engine states them, in a stable order.
        view.Origin.Should().Be(Origin);
        view.Edges.Should().HaveCount(2);
        view.Edges.Select(edge => edge.ConstraintName)
            .Should().Equal("orders_customer_id_fkey", "orders_shipping_fkey");
        view.Tables.Select(table => table.Table).Should().Equal("customers", "orders");
        view.Tables.Select(table => table.ForeignKeyCount).Should().Equal(0, 2);
        view.Tables.Sum(table => table.ForeignKeyCount).Should().Be(view.Edges.Count);
    }

    [Fact]
    public void Map_KeepsACapturedParentAndAnOutsideTargetApart()
    {
        // Arrange
        var model = RelationshipModelFixture.Model(
            RelationshipModelFixture.Table(
                "public",
                "customers",
                columns: [RelationshipModelFixture.Column("id", 1)],
                indexes: [RelationshipModelFixture.PrimaryIndex("id")]),
            RelationshipModelFixture.Table(
                "public",
                "orders",
                columns:
                [
                    RelationshipModelFixture.Column("id", 1),
                    RelationshipModelFixture.Column("customer_id", 2),
                    RelationshipModelFixture.Column("shipping_address_id", 3, nullable: true),
                ],
                foreignKeys:
                [
                    RelationshipModelFixture.ForeignKey(
                        "orders_customer_id_fkey", "public", "customers", ["customer_id"], ["id"]),
                    RelationshipModelFixture.ForeignKey(
                        "orders_shipping_fkey", "public", "addresses", ["shipping_address_id"], ["id"], deleteRule: "SET NULL"),
                ]));

        // Act
        var view = DatabaseModelGraphMapper.Map(model, Origin);

        // Assert
        var customersId = view.Tables.Single(table => table.Table == "customers").Id;
        var declared = view.Edges.Single(edge => edge.ConstraintName == "orders_customer_id_fkey");
        declared.ParentId.Should().Be(customersId);
        declared.ParentIsCaptured.Should().BeTrue();
        declared.IsSelfReference.Should().BeFalse();
        declared.IsMandatory.Should().BeTrue("customer_id is not nullable");
        declared.Columns.Should().Equal("customer_id");
        declared.ReferencedColumns.Should().Equal("id");
        declared.OnDelete.Should().BeNull("NO ACTION is the default the reader never needs to see");

        var outside = view.Edges.Single(edge => edge.ConstraintName == "orders_shipping_fkey");
        outside.ParentIsCaptured.Should().BeFalse();
        outside.IsMandatory.Should().BeFalse("shipping_address_id admits null");
        outside.OnDelete.Should().Be("SET NULL");
        outside.ParentId.Should().Be("o0001");
        view.OutsideTables.Should().ContainSingle()
            .Which.Should().Be(new RelationshipOutsideTableView("o0001", "public", "addresses"));
    }

    [Fact]
    public void Map_FlagsASelfReferenceOnTheTableThatDeclaresIt()
    {
        // Arrange
        var model = RelationshipModelFixture.Model(
            RelationshipModelFixture.Table(
                "public",
                "employees",
                columns:
                [
                    RelationshipModelFixture.Column("id", 1),
                    RelationshipModelFixture.Column("manager_id", 2, nullable: true),
                ],
                foreignKeys:
                [
                    RelationshipModelFixture.ForeignKey(
                        "employees_manager_fkey", "public", "employees", ["manager_id"], ["id"]),
                ]));

        // Act
        var view = DatabaseModelGraphMapper.Map(model, Origin);

        // Assert
        var edge = view.Edges.Should().ContainSingle().Subject;
        edge.IsSelfReference.Should().BeTrue();
        edge.ParentIsCaptured.Should().BeTrue();
        edge.ChildId.Should().Be(edge.ParentId);
        view.UnrelatedTableCount.Should().Be(0, "a self reference is still an incident edge");
    }

    [Fact]
    public void Map_TakesThePrimaryKeyFromTheIndexTheModelMarksPrimary()
    {
        // Arrange
        var model = RelationshipModelFixture.Model(
            RelationshipModelFixture.Table(
                "public",
                "orders",
                columns:
                [
                    RelationshipModelFixture.Column("id", 1),
                    RelationshipModelFixture.Column("customer_id", 2),
                ],
                indexes:
                [
                    RelationshipModelFixture.Index("orders_customer_id_idx", unique: false, ["customer_id"]),
                    RelationshipModelFixture.PrimaryIndex("id"),
                ]));

        // Act
        var view = DatabaseModelGraphMapper.Map(model, Origin);

        // Assert
        var table = view.Tables.Should().ContainSingle().Subject;
        table.PrimaryKey.Should().NotBeNull();
        table.PrimaryKey!.Name.Should().Be("test_pkey");
        table.PrimaryKey.Columns.Should().Equal("id");
        table.Columns.Single(column => column.Name == "id").IsPartOfPrimaryKey.Should().BeTrue();
        table.Columns.Single(column => column.Name == "customer_id").IsPartOfPrimaryKey.Should().BeFalse();
        table.Indexes.Should().HaveCount(2, "every index the model states is carried, the primary one included");
        table.IndexCount.Should().Be(table.Indexes.Count);
    }

    [Fact]
    public void Map_CarriesAnIndexDefinitionWhenStated_AndStatesNoneWhenNot()
    {
        // Arrange
        var model = RelationshipModelFixture.Model(
            RelationshipModelFixture.Table(
                "public",
                "orders",
                columns: [RelationshipModelFixture.Column("id", 1), RelationshipModelFixture.Column("state", 2)],
                indexes:
                [
                    RelationshipModelFixture.Index(
                        "orders_state_idx", unique: false, ["state"], "CREATE INDEX orders_state_idx ON public.orders (state)"),
                    RelationshipModelFixture.Index("orders_id_idx", unique: true, ["id"]),
                ]));

        // Act
        var view = DatabaseModelGraphMapper.Map(model, Origin);

        // Assert
        var indexes = view.Tables.Single().Indexes;
        indexes.Single(index => index.Name == "orders_state_idx").Definition
            .Should().Be("CREATE INDEX orders_state_idx ON public.orders (state)");
        indexes.Single(index => index.Name == "orders_id_idx").Definition.Should().BeNull();
        indexes.Select(index => index.Name).Should().Equal("orders_id_idx", "orders_state_idx");
    }

    [Fact]
    public void Map_CarriesUniqueConstraintsAndChecksByName()
    {
        // Arrange
        var model = RelationshipModelFixture.Model(
            RelationshipModelFixture.Table(
                "public",
                "orders",
                columns: [RelationshipModelFixture.Column("id", 1), RelationshipModelFixture.Column("code", 2)],
                checks:
                [
                    RelationshipModelFixture.Check("orders_code_length", "char_length(code) > 0"),
                    RelationshipModelFixture.Check("orders_code_alpha", "code ~ '^[a-z]+$'"),
                ],
                uniqueConstraints:
                [
                    RelationshipModelFixture.Unique("orders_code_key", "code"),
                    RelationshipModelFixture.Unique("orders_code_id_key", "code", "id"),
                ]));

        // Act
        var view = DatabaseModelGraphMapper.Map(model, Origin);

        // Assert
        var table = view.Tables.Single();
        table.UniqueKeys.Select(key => key.Name).Should().Equal("orders_code_id_key", "orders_code_key");
        table.UniqueKeys.Single(key => key.Name == "orders_code_id_key").Columns.Should().Equal("code", "id");
        table.CheckConstraints.Select(check => check.Name)
            .Should().Equal("orders_code_alpha", "orders_code_length");
        table.CheckConstraints.Single(check => check.Name == "orders_code_alpha").Expression
            .Should().Be("code ~ '^[a-z]+$'");
    }

    [Fact]
    public void Map_FlagsTheColumnsThatReferenceAndAreReferenced()
    {
        // Arrange
        var model = RelationshipModelFixture.Model(
            RelationshipModelFixture.Table(
                "public",
                "customers",
                columns:
                [
                    RelationshipModelFixture.Column("id", 1),
                    RelationshipModelFixture.Column("name", 2, comment: "the customer's own name"),
                ]),
            RelationshipModelFixture.Table(
                "public",
                "orders",
                columns:
                [
                    RelationshipModelFixture.Column("id", 1),
                    RelationshipModelFixture.Column("customer_id", 2),
                ],
                foreignKeys:
                [
                    RelationshipModelFixture.ForeignKey(
                        "orders_customer_id_fkey", "public", "customers", ["customer_id"], ["id"]),
                ]));

        // Act
        var view = DatabaseModelGraphMapper.Map(model, Origin);

        // Assert
        var customers = view.Tables.Single(table => table.Table == "customers");
        customers.Columns.Single(column => column.Name == "id").IsReferencedByForeignKey.Should().BeTrue();
        customers.Columns.Single(column => column.Name == "id").IsReferencingForeignKey.Should().BeFalse();
        customers.Columns.Single(column => column.Name == "name").Comment.Should().Be("the customer's own name");

        var orders = view.Tables.Single(table => table.Table == "orders");
        orders.Columns.Single(column => column.Name == "customer_id").IsReferencingForeignKey.Should().BeTrue();
        orders.Columns.Single(column => column.Name == "id").IsReferencingForeignKey.Should().BeFalse();
        orders.Columns.Select(column => column.Name).Should().Equal("id", "customer_id");
    }

    [Fact]
    public void Map_MakesAReferenceThatStatesNoTargetItsOwnNode()
    {
        // Arrange
        var model = RelationshipModelFixture.Model(
            RelationshipModelFixture.Table(
                "public",
                "ledger",
                columns: [RelationshipModelFixture.Column("id", 1)],
                foreignKeys: [RelationshipModelFixture.UnresolvedForeignKey("ledger_nameless_fkey", ["id"])]));

        // Act
        var view = DatabaseModelGraphMapper.Map(model, Origin);

        // Assert
        var childId = view.Tables.Single().Id;
        var edge = view.Edges.Should().ContainSingle().Subject;
        edge.ParentIsCaptured.Should().BeFalse();
        edge.ParentId.Should().Be($"unresolved:{childId}|ledger_nameless_fkey");
        view.OutsideTables.Should().ContainSingle()
            .Which.Should().Be(new RelationshipOutsideTableView(edge.ParentId, null, null));
    }

    [Fact]
    public void Map_MakesATargetInASchemaTheReadExcludedAnOutsideNode()
    {
        // Arrange — the reader excluded `auth`, so the constraint's parent is not in the model.
        var model = RelationshipModelFixture.Model(
            RelationshipModelFixture.Table(
                "public",
                "sessions",
                columns: [RelationshipModelFixture.Column("user_id", 1, dataType: "uuid")],
                foreignKeys:
                [
                    RelationshipModelFixture.ForeignKey(
                        "sessions_user_fkey", "auth", "users", ["user_id"], ["id"]),
                ]));

        // Act
        var view = DatabaseModelGraphMapper.Map(model, Origin);

        // Assert
        view.OutsideTables.Should().ContainSingle()
            .Which.Should().Be(new RelationshipOutsideTableView("o0001", "auth", "users"));
        view.Edges.Single().ParentId.Should().Be("o0001");
        view.Schemas.Should().ContainSingle()
            .Which.Should().Be(new RelationshipSchemaView("public", TableCount: 1, RelatedTableCount: 1));
    }

    [Fact]
    public void Map_AssignsIdsThatKeepDottedNamesApart()
    {
        // Arrange — `c`.`a.b` and `a`.`b.c` would collide if an id were built by joining names.
        var model = RelationshipModelFixture.Model(
            RelationshipModelFixture.Table(
                "a",
                "b.c",
                columns: [RelationshipModelFixture.Column("id", 1)]),
            RelationshipModelFixture.Table(
                "c",
                "a.b",
                columns: [RelationshipModelFixture.Column("id", 1)],
                foreignKeys:
                [
                    RelationshipModelFixture.ForeignKey("a_b_to_b_c", "a", "b.c", ["id"], ["id"]),
                ]));

        // Act
        var view = DatabaseModelGraphMapper.Map(model, Origin);

        // Assert
        view.Tables.Select(table => table.Id).Should().OnlyHaveUniqueItems();
        view.Tables.Select(table => table.Id).Should().Equal("t0001", "t0002");

        var parent = view.Tables.Single(table => table.Schema == "a" && table.Table == "b.c");
        var child = view.Tables.Single(table => table.Schema == "c" && table.Table == "a.b");
        var edge = view.Edges.Should().ContainSingle().Subject;
        edge.ChildId.Should().Be(child.Id);
        edge.ParentId.Should().Be(parent.Id);
        edge.ConstraintName.Should().Be("a_b_to_b_c");
        edge.Columns.Should().Equal("id");
        edge.ReferencedColumns.Should().Equal("id");
        edge.OnDelete.Should().BeNull();
        edge.OnUpdate.Should().BeNull();
        edge.IsMandatory.Should().BeTrue();
        edge.IsSelfReference.Should().BeFalse();
        edge.ParentIsCaptured.Should().BeTrue();
    }

    [Fact]
    public void Map_StatesNoRowCountAndNoCapturedData()
    {
        // Arrange — a live read never counts rows (D9), so every table states an absent count.
        var model = RelationshipModelFixture.Model(
            RelationshipModelFixture.Table("public", "customers", columns: [RelationshipModelFixture.Column("id", 1)]),
            RelationshipModelFixture.Table("public", "orders", columns: [RelationshipModelFixture.Column("id", 1)]));

        // Act
        var view = DatabaseModelGraphMapper.Map(model, Origin);

        // Assert
        view.HasData.Should().BeFalse();
        view.Tables.Should().OnlyContain(table => table.RowCount == null);
    }

    [Fact]
    public void Map_CountsTheTablesNoConstraintTouches()
    {
        // Arrange
        var model = RelationshipModelFixture.Model(
            RelationshipModelFixture.Table(
                "public",
                "customers",
                columns: [RelationshipModelFixture.Column("id", 1)]),
            RelationshipModelFixture.Table(
                "public",
                "orders",
                columns: [RelationshipModelFixture.Column("customer_id", 1)],
                foreignKeys:
                [
                    RelationshipModelFixture.ForeignKey(
                        "orders_customer_id_fkey", "public", "customers", ["customer_id"], ["id"]),
                ]),
            RelationshipModelFixture.Table("public", "audit_log", columns: [RelationshipModelFixture.Column("id", 1)]),
            RelationshipModelFixture.Table("sales", "order_items", columns: [RelationshipModelFixture.Column("id", 1)]));

        // Act
        var view = DatabaseModelGraphMapper.Map(model, Origin);

        // Assert
        view.UnrelatedTableCount.Should().Be(2, "audit_log and every sales table declare and receive no constraint");
        view.Schemas.Should().Equal(
            new RelationshipSchemaView("public", TableCount: 3, RelatedTableCount: 2),
            new RelationshipSchemaView("sales", TableCount: 1, RelatedTableCount: 0));
    }

    [Fact]
    public void Map_OfAnEmptyModel_StatesNoNodesAndNoEdges()
    {
        // Act
        var view = DatabaseModelGraphMapper.Map(RelationshipModelFixture.Model(), Origin);

        // Assert
        view.Tables.Should().BeEmpty();
        view.OutsideTables.Should().BeEmpty();
        view.Edges.Should().BeEmpty();
        view.Schemas.Should().BeEmpty();
        view.UnrelatedTableCount.Should().Be(0);
        view.HasData.Should().BeFalse();
    }

    [Fact]
    public void Map_RejectsAnAbsentModel()
    {
        // Act
        var act = () => DatabaseModelGraphMapper.Map(null!, Origin);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }
}
