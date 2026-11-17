using CanonicalModel.Interchange;

using DatabaseInterchange.Report;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Report;

/// <summary>
/// The relation analysis: one edge per captured constraint, the components that hold the register, the
/// cyclic components, and the two lists that keep self and external references out of both.
/// </summary>
/// <remarks>
/// The rules asserted here are §5.1, §5.4, §5.5, §5.7 and §5.8. Each one is about what the analysis may
/// <em>not</em> do: not merge two constraints into one, not call a self loop a cycle, not let a table the
/// catalog does not hold join two groups, not lose a view reference into the register.
/// </remarks>
public sealed class RelationGraphBuilderTests
{
    private readonly RelationGraphBuilder _builder = new();

    [Fact]
    public void Build_EmitsOneEdgePerCapturedConstraint()
    {
        // Arrange — three spokes, each declaring one constraint against the same hub.
        var snapshot = ReportModel.Star(3);

        // Act
        var map = _builder.Build(snapshot);

        // Assert
        map.ForeignKeyCount.Should().Be(3);
        map.Edges.Select(e => e.Constraint).Should().OnlyHaveUniqueItems();
        map.Groups.Should().ContainSingle();
        map.Groups[0].RelationCount.Should().Be(3);
        map.Groups[0].TableCount.Should().Be(4);
    }

    [Fact]
    public void Build_OrdersTheRegisterByChildThenParentThenConstraint()
    {
        // Arrange (§5.2): two children, one of them pointing into two schemas, twice at one of them.
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("billing", "invoices"),
            ReportModel.KeyedTable("public", "customers"),
            Dependent("public", "audit",
                ReferenceTo("public", "audit", "fk_audit_invoice", "billing", "invoices", "invoice_id")),
            Dependent("public", "orders",
                ReferenceTo("public", "orders", "zz_second", "public", "customers", "customer_id"),
                ReferenceTo("public", "orders", "aa_first", "public", "customers", "reviewed_by"),
                ReferenceTo("public", "orders", "fk_orders_billing", "billing", "invoices", "billing_id")));

        // Act
        var map = _builder.Build(snapshot);

        // Assert — "audit" before "orders" by child table; within one child, the billing parent before
        // the public one; within one parent pair, by constraint name.
        map.Edges.Select(e => $"{e.ChildName}|{e.ParentName}|{e.Constraint}").Should().Equal(
            "public.audit|billing.invoices|fk_audit_invoice",
            "public.orders|billing.invoices|fk_orders_billing",
            "public.orders|public.customers|aa_first",
            "public.orders|public.customers|zz_second");
    }

    [Fact]
    public void Build_KeepsThreeConstraintsBetweenOnePairAsThreeEntriesAndNamesThePairOnce()
    {
        // Arrange (§5.1: an edge is exactly one constraint).
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "customers"),
            Dependent("public", "orders",
                Reference("public", "orders", "fk_bill", "customers", "bill_to"),
                Reference("public", "orders", "fk_ship", "customers", "ship_to"),
                Reference("public", "orders", "fk_review", "customers", "reviewed_by")));

        // Act
        var map = _builder.Build(snapshot);

        // Assert
        map.Edges.Should().HaveCount(3);
        map.Groups[0].RelationCount.Should().Be(3);

        var pair = map.Coupling.DuplicatePairs.Should().ContainSingle().Subject;
        pair.Left.Should().Be("public.customers");
        pair.Right.Should().Be("public.orders");
        pair.Count.Should().Be(3);
        pair.Constraints.Should().Equal("fk_bill", "fk_review", "fk_ship");
    }

    [Fact]
    public void Build_DerivesRequiredFromTheNullabilityOfEveryReferencingColumn()
    {
        // Arrange — all non-null, one nullable, a composite with none nullable, and one with a mix.
        var lookup = ReportModel.KeyedTable("public", "lookup");
        var allNonNull = Dependent("public", "a", Reference("public", "a", "fk_a", "lookup", "ref_id"));
        var oneNullable = Dependent("public", "b", Reference("public", "b", "fk_b", "lookup", "ref_id")) with
        {
            Columns =
            [
                ReportModel.Column("public", "b", "id", nullable: false),
                ReportModel.Column("public", "b", "ref_id", nullable: true),
            ],
        };
        var composite = Dependent(
            "public", "c", Composite("public", "c", "fk_c", "lookup", ["ref_one", "ref_two"]));
        var compositeMixed = Dependent(
            "public", "d", Composite("public", "d", "fk_d", "lookup", ["ref_one", "ref_two"])) with
        {
            Columns =
            [
                ReportModel.Column("public", "d", "id", nullable: false),
                ReportModel.Column("public", "d", "ref_one", nullable: false),
                ReportModel.Column("public", "d", "ref_two", nullable: true),
            ],
        };

        // Act
        var map = _builder.Build(ReportModel.Snapshot(
            lookup, allNonNull, oneNullable, composite, compositeMixed));

        // Assert
        Edge(map, "fk_a").Required.Should().BeTrue();
        Edge(map, "fk_b").Required.Should().BeFalse("a nullable column lets the link stand unattached");
        Edge(map, "fk_c").Required.Should().BeTrue();
        Edge(map, "fk_d").Required.Should().BeFalse("one nullable column makes the whole composite optional");
    }

    [Fact]
    public void Build_CarriesTheReferentialActionsVerbatimFromTheCapture()
    {
        // Arrange (§5.1). The rendering of a default action is the renderer's rule, tested there.
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "customers"),
            ReportModel.Table("public", "orders",
                ReportModel.Column("public", "orders", "id", nullable: false),
                ReportModel.Column("public", "orders", "customer_id", nullable: false)) with
            {
                ForeignKeys =
                [
                    ReportModel.Reference(
                        "public", "orders", "fk_orders_customer", "public", "customers",
                        ["customer_id"], ["id"], onDelete: "CASCADE", onUpdate: "NO ACTION"),
                ],
            });

        // Act
        var map = _builder.Build(snapshot);

        // Assert
        var edge = map.Edges.Single();
        edge.OnDelete.Should().Be("CASCADE");
        edge.OnUpdate.Should().Be("NO ACTION");
        edge.Required.Should().BeTrue();
    }

    [Fact]
    public void Build_KeepsASelfReferenceOutOfEveryCyclicGroup()
    {
        // Arrange — the only cycle-shaped edge in the schema is employees.manager_id.
        var snapshot = ReportModel.Snapshot(
            Dependent("public", "employees",
                Reference("public", "employees", "fk_manager", "employees", "manager_id")));

        // Act
        var map = _builder.Build(snapshot);

        // Assert (§5.5)
        map.SelfReferences.Should().ContainSingle().Which.IsSelfReference.Should().BeTrue();
        map.CyclicGroups.Should().BeEmpty();
        map.TablesInCycles.Should().Be(0);
        map.Edges.Should().HaveCount(1);
        map.Cards.Should().ContainSingle().Which.CyclicGroup.Should().BeNull();

        // The constraint is still accounted for once in each direction of its own table (§5.3).
        var card = map.Cards.Single();
        card.Outgoing.Should().HaveCount(1);
        card.Incoming.Should().HaveCount(1);
        card.RelationCount.Should().Be(2);
    }

    [Fact]
    public void Build_ListsATableInACycle_AndStillListsItsSelfReference()
    {
        // Arrange — employees both references itself and takes part in a two-table cycle.
        var snapshot = ReportModel.Snapshot(
            Dependent("public", "employees",
                Reference("public", "employees", "fk_manager", "employees", "manager_id"),
                Reference("public", "employees", "fk_emp_dept", "departments", "dept_id")),
            Dependent("public", "departments",
                Reference("public", "departments", "fk_dept_head", "employees", "head_id")));

        // Act
        var map = _builder.Build(snapshot);

        // Assert
        map.CyclicGroups.Should().ContainSingle();
        map.CyclicGroups[0].Members.Should().Equal("public.departments", "public.employees");
        map.CyclicGroups[0].InternalConstraintCount.Should().Be(2);
        map.SelfReferences.Should().ContainSingle().Which.Constraint.Should().Be("fk_manager");
        map.Cards.Should().HaveCount(2);
        map.Cards.Should().OnlyContain(card => card.CyclicGroup == 1);
    }

    [Fact]
    public void Build_ReportsComponentsNotCycles()
    {
        // Arrange — six tables whose ring closes back on itself, plus two chords: many cycles, one
        // component (§5.5).
        var ring = new (string Child, string Name, string Parent)[]
        {
            ("n1", "e_1_2", "n2"), ("n2", "e_2_3", "n3"), ("n3", "e_3_4", "n4"),
            ("n4", "e_4_5", "n5"), ("n5", "e_5_6", "n6"), ("n6", "e_6_1", "n1"),
            ("n2", "e_2_5", "n5"), ("n3", "e_3_6", "n6"),
        };
        var names = Enumerable.Range(1, 6).Select(i => $"n{i}").ToArray();
        var tables = names.Select(n => ReportModel.KeyedTable("public", n)).ToList();

        foreach (var name in names)
        {
            var outgoing = ring.Where(e => e.Child == name).ToList();
            if (outgoing.Count > 0)
            {
                tables[Array.IndexOf(names, name)] = Dependent("public", name, [
                    .. outgoing.Select(e => Reference("public", name, e.Name, e.Parent, $"ref_{e.Name}")),
                ]);
            }
        }

        // Act
        var map = _builder.Build(ReportModel.Snapshot([.. tables]));

        // Assert — one component holding all six tables, and its true constraint total.
        map.CyclicGroups.Should().ContainSingle();
        var group = map.CyclicGroups[0];
        group.Members.Should().Equal(names.Select(n => $"public.{n}"));
        group.InternalConstraintCount.Should().Be(8);
        group.Constraints.Should().BeInAscendingOrder(c => c, StringComparer.Ordinal);

        // One example path, shorter than the whole: which is what makes it an example and not a claim
        // about how entangled the component is. Which of the equally short cycles is chosen is the
        // analysis's own business — what the reader is owed is a real, closed, unrepeated short example.
        group.PathConstraintCount.Should().Be(4);
        group.PathConstraintCount.Should().BeLessThan(group.InternalConstraintCount);
        group.PathTables.Should().HaveCount(5);
        group.PathTables[0].Should().Be(
            group.PathTables[^1], "an example path closes on the table it starts from");
        group.PathTables.Distinct().Should().HaveCount(4, "a shortest cycle repeats no table but its end");
        group.PathTables.Zip(group.PathTables.Skip(1)).Should().OnlyContain(pair =>
            map.Edges.Any(edge => edge.ChildName == pair.First && edge.ParentName == pair.Second));

        map.Cards.Where(card => group.Members.Contains(card.QualifiedName))
            .Should().OnlyContain(card => card.CyclicGroup == group.Number);
    }

    [Fact]
    public void Build_LeavesAnUpstreamChainOutOfTheComponentItPointsInto()
    {
        // Arrange — audit only reads orders; orders and invoices are the cycle (§5.5).
        var snapshot = ReportModel.Snapshot(
            Dependent("public", "audit", Reference("public", "audit", "fk_audit_order", "orders", "order_id")),
            Dependent("public", "orders", Reference("public", "orders", "fk_order_invoice", "invoices", "invoice_id")),
            Dependent("public", "invoices", Reference("public", "invoices", "fk_invoice_order", "orders", "order_id")));

        // Act
        var map = _builder.Build(snapshot);

        // Assert
        map.CyclicGroups.Should().ContainSingle().Which.Members.Should().Equal(
            "public.invoices", "public.orders");
        map.TablesInCycles.Should().Be(2);
        map.Cards.Single(card => card.Table == "audit").CyclicGroup.Should().BeNull();

        // The component is still one undirected group holding all three tables and all three constraints.
        map.Groups.Should().ContainSingle().Which.TableCount.Should().Be(3);
        map.Groups[0].RelationCount.Should().Be(3);
    }

    [Fact]
    public void Build_KeepsViewReferencesOutOfTheRegisterAndInLineage()
    {
        // Arrange — one real constraint, two view reads and one dependency of another kind.
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "customers"),
            Dependent("public", "orders",
                Reference("public", "orders", "fk_orders_customer", "customers", "customer_id"))) with
        {
            Views =
            [
                new ViewDefinition
                {
                    Ref = ObjectRef.View("sales", "active_customers"),
                    Name = "active_customers",
                    SchemaRef = ObjectRef.Schema("sales"),
                },
            ],
            Relationships =
            [
                new Relationship
                {
                    DependentRef = ObjectRef.View("sales", "active_customers"),
                    DependencyRef = ObjectRef.Table("public", "customers"),
                    Type = "viewReference",
                },
                new Relationship
                {
                    DependentRef = ObjectRef.View("sales", "another_view"),
                    DependencyRef = ObjectRef.View("sales", "active_customers"),
                    Type = "viewReference",
                },
                new Relationship
                {
                    DependentRef = ObjectRef.Table("public", "orders"),
                    DependencyRef = ObjectRef.Table("public", "customers"),
                    Type = "checkConstraint",
                },
            ],
        };

        // Act
        var map = _builder.Build(snapshot);

        // Assert (§5.1, §5.8)
        map.ForeignKeyCount.Should().Be(1);
        map.Lineage.Should().HaveCount(2);
        map.Lineage.Select(pair => $"{pair.Dependent} → {pair.Target}").Should().Equal(
            "sales.active_customers → public.customers",
            "sales.another_view → sales.active_customers");
    }

    [Fact]
    public void Build_KeepsAnExternalTargetInRegisterWithoutMergingOrSplittingAGroup()
    {
        // Arrange — two components, each of them also pointing at one and the same table the catalog
        // does not hold. Were that missing parent a member, the two components would share it and
        // collapse into one group (§5.7).
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "customers"),
            Dependent("public", "orders",
                Reference("public", "orders", "fk_orders_customer", "customers", "customer_id"),
                Ghost("public", "orders", "fk_orders_ghost")),
            ReportModel.KeyedTable("public", "regions"),
            Dependent("public", "stores",
                Reference("public", "stores", "fk_store_region", "regions", "region_id"),
                Ghost("public", "stores", "fk_stores_ghost")));

        // Act
        var map = _builder.Build(snapshot);

        // Assert — both rows stay, each names its parent and says the catalog does not hold it.
        map.ExternalReferences.Should().HaveCount(2);
        map.ExternalReferences.Select(e => e.Constraint).Should().Equal(
            "fk_orders_ghost", "fk_stores_ghost");
        map.ExternalReferences.Should().OnlyContain(edge =>
            edge.ParentName == "archive.gone" && !edge.ParentIsCaptured);
        map.Edges.Should().Contain(edge => edge.Constraint == "fk_orders_ghost");

        // No card for the missing parent, and the two components are what they would be without it.
        map.Cards.Should().NotContain(card => card.QualifiedName == "archive.gone");
        map.Groups.Should().HaveCount(2);
        map.Groups[0].Members.Should().Equal("public.customers", "public.orders");
        map.Groups[1].Members.Should().Equal("public.regions", "public.stores");

        // The child still carries the relation in its fan-out (§5.4).
        map.Cards.Single(card => card.Table == "orders").Outgoing.Should().HaveCount(2);
    }

    [Fact]
    public void Build_ReportsATargetTheConstraintDoesNotNameAsOutsideTheCatalog()
    {
        // Arrange (§5.1's fallback: a reference that names no schema, and one that names nothing).
        var orders = ReportModel.Table("public", "orders",
            ReportModel.Column("public", "orders", "id", nullable: false)) with
        {
            ForeignKeys =
            [
                ReportModel.UnresolvedReference("public", "orders", "fk_unnamed", new ObjectRef("table:lost"), "id"),
                ReportModel.UnresolvedReference("public", "orders", "fk_empty", new ObjectRef(string.Empty), "id"),
            ],
        };

        // Act
        var map = _builder.Build(ReportModel.Snapshot(orders));

        // Assert — both are counted, both are external, neither is silently dropped.
        map.ForeignKeyCount.Should().Be(2);
        map.ExternalReferences.Should().HaveCount(2);
        Edge(map, "fk_unnamed").ParentTable.Should().Be(string.Empty);
        Edge(map, "fk_unnamed").ParentSchema.Should().Be(string.Empty);
        Edge(map, "fk_empty").ParentSchema.Should().Be(string.Empty);
    }

    [Fact]
    public void Build_YieldsTheSameComponentsAcrossBuildsAndIgnoresTableInsertionOrder()
    {
        // Arrange — the same catalog with the first table moved to the back.
        var ordered = ReportModel.Star(6);
        var rotated = ReportModel.Snapshot(
            [.. ordered.Tables.Skip(1).Append(ordered.Tables[0])]);

        // Act
        var first = _builder.Build(ordered);
        var second = _builder.Build(rotated);

        // Assert (§7: components identical across two builds of the same snapshot).
        second.Groups.SelectMany(group => group.Members).Should().Equal(
            first.Groups.SelectMany(group => group.Members));
        second.Edges.Select(edge => edge.Constraint).Should().Equal(
            first.Edges.Select(edge => edge.Constraint));
        second.Cards.Select(card => card.QualifiedName).Should().Equal(
            first.Cards.Select(card => card.QualifiedName));
        second.CyclicGroups.Should().BeEmpty();
    }

    [Fact]
    public void Build_RanksCouplingByCountThenName_AndBoundsOnlyTheRanking()
    {
        // Arrange — more related pairs than the ranking may print, all tied on one relation.
        var snapshot = Pairs(ReportBudget.MaxCouplingRows + 5);

        // Act
        var map = _builder.Build(snapshot);

        // Assert
        map.Coupling.FanIn.Should().HaveCount(ReportBudget.MaxCouplingRows);
        map.Coupling.FanIn.Select(d => d.Count).Should().OnlyContain(count => count == 1);
        map.Coupling.FanIn[0].Table.Should().Be("public.par_0000");
        map.Coupling.FanOut[0].Table.Should().Be("public.dep_0000");

        // Every table is still accounted for outside the ranking: the cards hold all of them, and the
        // referenced half of each pair carries no foreign key of its own (§5.4).
        map.Cards.Should().HaveCount((ReportBudget.MaxCouplingRows + 5) * 2);
        map.Coupling.TablesWithForeignKeys.Should().Be(ReportBudget.MaxCouplingRows + 5);
        map.Coupling.TablesWithoutForeignKeys.Should().Be(ReportBudget.MaxCouplingRows + 5);
    }

    [Fact]
    public void Build_CountsCrossSchemaRelationsByDirection()
    {
        // Arrange
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("billing", "invoices"),
            ReportModel.KeyedTable("public", "customers"),
            Dependent("public", "orders",
                ReferenceTo("public", "orders", "fk_order_invoice", "billing", "invoices", "invoice_id")),
            Dependent("billing", "refunds",
                ReferenceTo("billing", "refunds", "fk_refund_customer", "public", "customers", "customer_id")));

        // Act
        var map = _builder.Build(snapshot);

        // Assert — the parent lives in the other schema in both cases, so both cross.
        map.Coupling.CrossSchemaTotal.Should().Be(2);
        map.Coupling.CrossSchema.Select(flow => $"{flow.From}→{flow.To} {flow.Count}").Should().Equal(
            "billing→public 1", "public→billing 1");
    }

    [Fact]
    public void Build_ListsTablesThatTakePartInNoRelationship()
    {
        // Arrange
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "lookup"),
            Dependent("public", "orders", Reference("public", "orders", "fk_orders_lookup", "lookup", "lookup_id")),
            ReportModel.KeyedTable("public", "standalone"));

        // Act
        var map = _builder.Build(snapshot);

        // Assert (§5.8) — a referenced table takes part in a relation, so only the third is listed here.
        map.TablesWithNoRelationships.Should().Equal("public.standalone");
        map.Coupling.TablesWithoutForeignKeys.Should().Be(2);
    }

    [Fact]
    public void Build_RejectsAMissingSnapshot()
    {
        var act = () => _builder.Build(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static ForeignKeyEdge Edge(RelationMap map, string constraint) =>
        map.Edges.Single(edge => edge.Constraint == constraint);

    /// <summary>A constraint against a table the catalog does not hold, in a schema of its own.</summary>
    private static ForeignKeyDefinition Ghost(string childSchema, string childTable, string name) =>
        ReportModel.Reference(
            childSchema, childTable, name, "archive", "gone", ["ghost_id"], ["id"]);

    /// <summary>One parent and one dependent per index, joined by exactly one constraint.</summary>
    private static InterchangeSnapshot Pairs(int count)
    {
        var tables = new List<TableDefinition>();

        for (var index = 0; index < count; index++)
        {
            var parent = $"par_{index:0000}";
            tables.Add(ReportModel.KeyedTable("public", parent));
            tables.Add(Dependent("public", $"dep_{index:0000}",
                Reference("public", $"dep_{index:0000}", $"fk_{index:0000}", parent, "parent_id")));
        }

        return ReportModel.Snapshot([.. tables]);
    }

    /// <summary>A table holding one non-nullable column per reference, so a link can be required.</summary>
    private static TableDefinition Dependent(
        string schema, string name, params ForeignKeyDefinition[] references)
    {
        var columns = new List<ColumnDefinition>
        {
            ReportModel.Column(schema, name, "id", nullable: false),
        };
        columns.AddRange(references
            .SelectMany(reference => reference.Columns)
            .Distinct(StringComparer.Ordinal)
            .Select(column => ReportModel.Column(schema, name, column, nullable: false)));

        return ReportModel.Table(schema, name, [.. columns]) with
        {
            PrimaryKey = ReportModel.PrimaryKey(schema, name, "id"),
            ForeignKeys = [.. references],
        };
    }

    private static ForeignKeyDefinition Reference(
        string childSchema, string childTable, string name, string parentTable, string column) =>
        ReportModel.Reference(
            childSchema, childTable, name, childSchema, parentTable, [column], ["id"]);

    /// <summary>A reference whose parent lives in a schema the short helper cannot state.</summary>
    private static ForeignKeyDefinition ReferenceTo(
        string childSchema, string childTable, string name, string parentSchema, string parentTable,
        string column) =>
        ReportModel.Reference(
            childSchema, childTable, name, parentSchema, parentTable, [column], ["id"]);

    private static ForeignKeyDefinition Composite(
        string childSchema, string childTable, string name, string parentTable, string[] columns) =>
        ReportModel.Reference(
            childSchema, childTable, name, childSchema, parentTable,
            [.. columns], ["id", "name"]);
}
