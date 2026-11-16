using DbAdapter.PostgreSql.Pipeline;

using FluentAssertions;

using CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// Tests for <see cref="ForeignKeyScopePlan"/>: the one predicate that decides whether a
/// foreign key may be created — the parent table is in the copied model and was created.
/// Pure: no database, no connection, no DDL.
/// </summary>
public class ForeignKeyScopePlanTests
{
    [Fact]
    public void Build_ParentInsideModel_ConstraintIsInScope()
    {
        var model = Model(
            Table("public", "users"),
            Table("public", "orders", Fk("orders_owner_id_fkey", "public", "users")));

        var plan = ForeignKeyScopePlan.Build(model, new HashSet<TableId>());

        plan.Deferred.Should().BeEmpty();
        plan.InScope.Should().ContainSingle();
        plan.InScope[0].Table.Should().Be(new TableId("public", "orders"));
        plan.InScope[0].ForeignKeys.Should().ContainSingle()
            .Which.Name.Should().Be("orders_owner_id_fkey");
        plan.InScopeCount.Should().Be(1);
        plan.DeferredCount.Should().Be(0);
    }

    [Fact]
    public void Build_ParentInExcludedSchema_IsDeferredOncePerParent()
    {
        // auth is absent from the model: ExcludeSchemas already removed its tables.
        var model = Model(
            Table("public", "users"),
            Table(
                "public", "orders",
                Fk("orders_created_by_fkey", "auth", "users"),
                Fk("orders_deleted_by_fkey", "auth", "users")),
            Table("public", "comments", Fk("comments_created_by_fkey", "auth", "users")));

        var plan = ForeignKeyScopePlan.Build(model, new HashSet<TableId>());

        plan.InScope.Should().BeEmpty();
        plan.Deferred.Should().ContainSingle();
        plan.Deferred[0].Parent.Should().Be(new TableId("auth", "users"));
        plan.Deferred[0].Count.Should().Be(3, "the group counts the constraints, not the tables");
        plan.DeferredCount.Should().Be(3);
    }

    [Fact]
    public void Build_ParentPresentButSkipped_IsDeferred()
    {
        var model = Model(
            Table("public", "users"),
            Table("public", "orders", Fk("orders_owner_id_fkey", "public", "users")));
        var skipped = new HashSet<TableId> { new("public", "users") };

        var plan = ForeignKeyScopePlan.Build(model, skipped);

        plan.InScope.Should().BeEmpty();
        plan.Deferred.Should().ContainSingle();
        plan.Deferred[0].Parent.Should().Be(new TableId("public", "users"));
        plan.Deferred[0].Count.Should().Be(1);
    }

    [Fact]
    public void Build_ParentNamedInDifferentCase_IsInScope()
    {
        // PostgreSQL folds unquoted identifiers, so the lookup goes through TableId.
        var model = Model(
            Table("public", "Users"),
            Table("public", "orders", Fk("orders_owner_id_fkey", "PUBLIC", "USERS")));

        var plan = ForeignKeyScopePlan.Build(model, new HashSet<TableId>());

        plan.Deferred.Should().BeEmpty();
        plan.InScopeCount.Should().Be(1);
    }

    [Fact]
    public void Build_SelfReference_IsInScope()
    {
        var model = Model(
            Table("public", "nodes", Fk("nodes_parent_id_fkey", "public", "nodes")));

        var plan = ForeignKeyScopePlan.Build(model, new HashSet<TableId>());

        plan.Deferred.Should().BeEmpty();
        plan.InScopeCount.Should().Be(1);
    }

    [Fact]
    public void Build_ChildWithMixedScope_KeepsOnlyTheInScopeConstraints()
    {
        var model = Model(
            Table("public", "users"),
            Table(
                "public", "orders",
                Fk("orders_owner_id_fkey", "public", "users"),
                Fk("orders_created_by_fkey", "auth", "users")));

        var plan = ForeignKeyScopePlan.Build(model, new HashSet<TableId>());

        plan.InScope.Should().ContainSingle();
        plan.InScope[0].ForeignKeys.Should().ContainSingle()
            .Which.Name.Should().Be("orders_owner_id_fkey");
        plan.Deferred.Should().ContainSingle()
            .Which.Parent.Should().Be(new TableId("auth", "users"));
    }

    [Fact]
    public void Build_DeferredGroups_AreOrderedBySchemaThenName()
    {
        var model = Model(
            Table(
                "public", "orders",
                Fk("fk_zzz", "zzz", "alpha"),
                Fk("fk_users", "auth", "users"),
                Fk("fk_accounts", "auth", "accounts")));

        var plan = ForeignKeyScopePlan.Build(model, new HashSet<TableId>());

        plan.Deferred.Select(g => g.Parent.FullName)
            .Should().Equal("auth.accounts", "auth.users", "zzz.alpha");
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static ForeignKeyDefinition Fk(string name, string parentSchema, string parentTable) =>
        new(name, ["owner_id"], parentSchema, parentTable, ["id"], "NO ACTION", "NO ACTION", false, false);

    private static TableDefinition Table(
        string schema, string name, params ForeignKeyDefinition[] foreignKeys) =>
        new(schema, name, [], [], foreignKeys, [], [], null, false, null, null);

    private static DatabaseModel Model(params TableDefinition[] tables) =>
        new(
            DatabaseName: "testdb",
            ServerVersion: "17.0",
            Schemas: [],
            Tables: tables,
            Views: [],
            MaterializedViews: [],
            Sequences: [],
            Enums: [],
            Domains: [],
            CompositeTypes: [],
            Functions: [],
            Triggers: [],
            Policies: [],
            Publications: [],
            Subscriptions: [],
            Extensions: []);
}
