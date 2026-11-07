using CanonicalModel.Interchange;

using DbAdapter.PostgreSql.Interchange;

using FluentAssertions;

using Models = CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// Hermetic tests for the conversion from the operational model to the catalog's preservation
/// model. Nothing here opens a connection: the mapper is a pure function over the records in
/// <see cref="InterchangeTestModel"/>, which is exactly why the two model namespaces can be
/// checked against each other without a server.
/// </summary>
public class DatabaseModelMapperTests
{
    private const string Dialect = "postgresql";

    private static readonly DatabaseModelMapper Mapper = new(Dialect);

    private static SnapshotSchema MapFull() => Mapper.Map(InterchangeTestModel.Full());

    [Fact]
    public void Map_CarriesTheDatabaseIdentity()
    {
        var snapshot = MapFull();

        snapshot.Database.Name.Should().Be("testdb");
        snapshot.Database.ServerVersion.Should().Be("PostgreSQL 16.0");
    }

    [Fact]
    public void Map_SchemasAreSortedAndCarryTheirOwner()
    {
        var snapshot = MapFull();

        snapshot.Schemas.Select(s => s.Name).Should().Equal("public", "reporting");
        snapshot.Schemas.Should().OnlyContain(s => s.Owner == "test_user");
    }

    [Fact]
    public void Map_TablesAreSortedAndPointAtTheirSchemaById()
    {
        var snapshot = MapFull();
        var publicId = snapshot.Schemas.Single(s => s.Name == "public").Id;

        snapshot.Tables.Select(t => t.SourceObjectId).Should()
            .Equal("public.customers", "public.orders", "reporting.key_value");

        var customers = snapshot.Tables.Single(t => t.Name == "customers");
        customers.SchemaId.Should().Be(publicId);
        customers.TableKind.Should().Be(ETableKind.Table);
    }

    [Fact]
    public void Map_ColumnKeepTheirOrdinalNativeTextAndDeclaredModifiers()
    {
        var customers = MapFull().Tables.Single(t => t.Name == "customers");

        customers.Columns.Select(c => c.Name).Should()
            .Equal("id", "name", "balance", "status", "created_at", "total");

        var name = customers.Columns.Single(c => c.Name == "name");
        name.NativeType.NativeName.Should().Be("character varying(80)");
        name.LogicalType.Should().Be(ELogicalType.String);
        name.Length.Should().Be(80);
        name.Nullable.Should().BeFalse();

        var balance = customers.Columns.Single(c => c.Name == "balance");
        balance.LogicalType.Should().Be(ELogicalType.Decimal);
        balance.Precision.Should().Be(10);
        balance.Scale.Should().Be(2);
        balance.DefaultValue!.Text.Should().Be("2");
        balance.DefaultValue.Dialect.Should().Be(Dialect);

        customers.Columns.Single(c => c.Name == "created_at")
            .LogicalType.Should().Be(ELogicalType.TimestampWithTimeZone);

        // The enum column is recognized from the database's own type inventory, not guessed.
        var status = customers.Columns.Single(c => c.Name == "status");
        status.LogicalType.Should().Be(ELogicalType.Enum);
        status.NativeType.NativeName.Should().Be("mood");

        var total = customers.Columns.Single(c => c.Name == "total");
        total.Generated!.Text.Should().Be("(id * 2)");

        // The metadata model reports identity as a flag, not as ALWAYS or BY DEFAULT, so the
        // column records the fact without inventing the mode.
        customers.Columns.Single(c => c.Name == "id").Identity.Should().Be("yes");
        customers.Columns.Single(c => c.Name == "id").Description.Should().Be("primary key");
    }

    [Fact]
    public void Map_ColumnsBelongToTheirTableAndHaveDistinctIds()
    {
        var snapshot = MapFull();
        var tableIds = snapshot.Tables.Select(t => t.Id).ToList();

        foreach (var table in snapshot.Tables)
        {
            table.Columns.Should().OnlyContain(c => c.TableId == table.Id);
        }

        var columnIds = snapshot.Tables.SelectMany(t => t.Columns).Select(c => c.Id).ToList();
        columnIds.Should().OnlyHaveUniqueItems();
        columnIds.Intersect(tableIds).Should().BeEmpty();
    }

    [Fact]
    public void Map_PrimaryKeyComesFromThePrimaryIndexWithCompleteServerText()
    {
        var customers = MapFull().Tables.Single(t => t.Name == "customers");

        var primaryKey = customers.PrimaryKey!;
        primaryKey.Name.Should().Be("pk_customers");
        primaryKey.Columns.Should().Equal("id");
        primaryKey.TableId.Should().Be(customers.Id);
        primaryKey.NativeDefinition!.Complete.Should().BeTrue();
        primaryKey.NativeDefinition.CapturedBy.Should().Be("pg_get_indexdef");
        primaryKey.NativeDefinition.Text.Should().StartWith("CREATE UNIQUE INDEX pk_customers");
    }

    [Fact]
    public void Map_KeylessTableHasNoPrimaryKeyAndDeclaresSourceOrder()
    {
        var keyValue = MapFull().Tables.Single(t => t.Name == "key_value");

        keyValue.PrimaryKey.Should().BeNull();
        keyValue.RowOrder.Should().Be(ERowOrder.SourceOrder);

        MapFull().Tables.Where(t => t.Name != "key_value")
            .Should().OnlyContain(t => t.RowOrder == ERowOrder.PrimaryKey);
    }

    [Fact]
    public void Map_IndexWithoutServerTextCarriesNoNativeDefinition()
    {
        var customers = MapFull().Tables.Single(t => t.Name == "customers");

        // The primary key's index is reported by the server, a plain secondary index here is not.
        customers.Indexes.Should().HaveCount(2);
        customers.Indexes.Single(i => i.Name == "ix_customers_name").NativeDefinition.Should().BeNull();
        customers.Indexes.Single(i => i.Name == "ix_customers_name").IsUnique.Should().BeFalse();
    }

    [Fact]
    public void Map_ForeignKeyPointsAtTheReferencedTableAndKeepsTheRulesVerbatim()
    {
        var snapshot = MapFull();
        var customers = snapshot.Tables.Single(t => t.Name == "customers");
        var orders = snapshot.Tables.Single(t => t.Name == "orders");

        var foreignKey = orders.ForeignKeys.Single();
        foreignKey.Name.Should().Be("fk_orders_customer");
        foreignKey.TableId.Should().Be(orders.Id);
        foreignKey.Columns.Should().Equal("customer_id");
        foreignKey.ReferencedTableId.Should().Be(customers.Id);
        foreignKey.ReferencedColumns.Should().Equal("id");
        foreignKey.OnDelete.Should().Be("CASCADE");
        foreignKey.OnUpdate.Should().Be("NO ACTION");
    }

    [Fact]
    public void Map_CheckAndUniqueConstraintsHangUnderTheirTable()
    {
        var customers = MapFull().Tables.Single(t => t.Name == "customers");

        var check = customers.CheckConstraints.Single();
        check.Name.Should().Be("chk_balance");
        check.Expression.Text.Should().Be("balance >= 0::numeric");
        check.Expression.Dialect.Should().Be(Dialect);
        check.TableId.Should().Be(customers.Id);

        var unique = customers.UniqueConstraints.Single();
        unique.Name.Should().Be("uq_customers_name");
        unique.Columns.Should().Equal("name");
    }

    [Fact]
    public void Map_TriggersHangUnderTheirOwnTableAndReferenceTheirRoutine()
    {
        var snapshot = MapFull();
        var customers = snapshot.Tables.Single(t => t.Name == "customers");
        var orders = snapshot.Tables.Single(t => t.Name == "orders");
        var routine = snapshot.Routines.Single(r => r.Name == "fn_notify");

        var audit = customers.Triggers.Single();
        audit.Name.Should().Be("tr_audit");
        audit.TableId.Should().Be(customers.Id);
        audit.Timing.Should().Be(ETriggerTiming.Before);
        audit.Events.Should().Be(ETriggerEvent.Insert | ETriggerEvent.Update);
        audit.Orientation.Should().Be(ETriggerOrientation.Row);
        audit.EnabledState.Should().Be("O");
        audit.When!.Text.Should().Be("NEW.id IS NOT NULL");
        audit.ReferencedRoutineId.Should().Be(routine.Id);

        var disabled = orders.Triggers.Single();
        disabled.Name.Should().Be("tr_disabled");
        disabled.TableId.Should().Be(orders.Id);
        disabled.Orientation.Should().Be(ETriggerOrientation.Statement);
        disabled.EnabledState.Should().Be("D");
        disabled.When.Should().BeNull();
    }

    [Fact]
    public void Map_ViewsIncludeMaterializedViewsAndMarkTheirTextAsAFragment()
    {
        var snapshot = MapFull();

        snapshot.Views.Should().HaveCount(2);

        var view = snapshot.Views.Single(v => v.Name == "v_active_customers");
        view.IsMaterialized.Should().BeFalse();
        view.Definition!.Text.Should().StartWith("SELECT id, name FROM public.customers");
        view.NativeDefinition!.Complete.Should().BeFalse();
        view.NativeDefinition.CapturedBy.Should().Be("pg_get_viewdef");

        var materialized = snapshot.Views.Single(v => v.Name == "mv_totals");
        materialized.IsMaterialized.Should().BeTrue();
        materialized.SchemaId.Should().Be(snapshot.Schemas.Single(s => s.Name == "reporting").Id);
    }

    [Fact]
    public void Map_RoutinesSeparateProceduresAndCarryTheCapturedImplementation()
    {
        var snapshot = MapFull();

        snapshot.Routines.Select(r => r.Name).Should().Equal("add_customer", "fn_notify", "run_cleanup");

        var procedure = snapshot.Routines.Single(r => r.Name == "run_cleanup");
        procedure.Kind.Should().Be(EObjectKind.Procedure);
        procedure.Implementations.Should().ContainSingle().Which.Dialect.Should().Be(Dialect);
        procedure.Implementations[0].Origin.Should().Be(EOrigin.Captured);
        procedure.Implementations[0].Language.Should().Be("plpgsql");
        procedure.Implementations[0].Body.Should().StartWith("CREATE PROCEDURE");
        procedure.Implementations[0].Requirements!.RequiredLanguage.Should().Be("plpgsql");
        procedure.NativeDefinition!.Complete.Should().BeTrue();
        procedure.NativeDefinition.CapturedBy.Should().Be("pg_get_functiondef");

        var function = snapshot.Routines.Single(r => r.Name == "add_customer");
        function.Kind.Should().Be(EObjectKind.Function);
        function.ReturnType.Should().Be("integer");
        function.SourceObjectId.Should().Be("public.add_customer(character varying)");
        function.Parameters.Should().ContainSingle().Which.Mode.Should().Be("IN");
    }

    [Fact]
    public void Map_SequencesRecordTheStateThatWasNotCaptured()
    {
        // Loss is recorded, not hidden: the current value is live server state the metadata model
        // does not carry, so the sequence says so instead of leaving the reader to assume.
        var note = MapFull().Sequences.Single().Notes.Single();

        note.Code.Should().Be("SequenceStateNotCaptured");
        note.Severity.Should().Be(EIssueSeverity.Info);
    }

    [Fact]
    public void Map_SequencesCarryTheirDeclaration()
    {
        var sequence = MapFull().Sequences.Single();

        sequence.Name.Should().Be("order_seq");
        sequence.DataType.Should().Be("bigint");
        sequence.StartValue.Should().Be(1);
        sequence.Increment.Should().Be(1);
        sequence.Cache.Should().Be(1);
        sequence.Cycle.Should().BeFalse();
        sequence.CurrentValue.Should().BeNull();
    }

    [Fact]
    public void Map_ExtensionsAreDatabaseScopedAndTheirTextIsSynthesized()
    {
        var extension = MapFull().Extensions.Single();

        extension.Name.Should().Be("pgcrypto");
        extension.Version.Should().Be("1.3");
        extension.Schema.Should().Be("ext");
        extension.NativeDefinition!.Complete.Should().BeFalse();
        extension.NativeDefinition.CapturedBy.Should().Be("synthesized");
    }

    [Fact]
    public void Map_SecurityCarriesPoliciesAndLeavesUncapturedPrincipalsEmpty()
    {
        var snapshot = MapFull();
        var customers = snapshot.Tables.Single(t => t.Name == "customers");

        var policy = snapshot.Security!.Policies.Single();
        policy.Name.Should().Be("customers_read");
        policy.TableId.Should().Be(customers.Id);
        policy.Command.Should().Be("SELECT");
        policy.IsPermissive.Should().BeTrue();
        policy.ApplicableRoles.Should().Equal("test_user");
        policy.Using!.Text.Should().Be("true");
        policy.WithCheck.Should().BeNull();

        // Absent, not claimed: roles and grants are not part of the metadata model.
        snapshot.Security.Roles.Should().BeEmpty();
        snapshot.Security.Grants.Should().BeEmpty();
    }

    [Fact]
    public void Map_RelationshipsNameForeignKeysAndViewReadsAndSkipUnknownRelations()
    {
        var snapshot = MapFull();
        var customers = snapshot.Tables.Single(t => t.Name == "customers");
        var orders = snapshot.Tables.Single(t => t.Name == "orders");
        var view = snapshot.Views.Single(v => v.Name == "v_active_customers");
        var matView = snapshot.Views.Single(v => v.Name == "mv_totals");

        // The FK plus one read edge per known relation. "public.ghost" is read by the view but is
        // not in the catalog, so no edge may point at it.
        snapshot.Relationships.Should().HaveCount(3);

        snapshot.Relationships.Should().Contain(r =>
            r.Type == "foreignKey" && r.DependentId == orders.Id && r.DependencyId == customers.Id);
        snapshot.Relationships.Should().Contain(r =>
            r.Type == "viewReference" && r.DependentId == view.Id && r.DependencyId == customers.Id);
        snapshot.Relationships.Should().Contain(r =>
            r.Type == "viewReference" && r.DependentId == matView.Id && r.DependencyId == orders.Id);

        // No edge may dangle: every dependency has to name a table the catalog actually captured.
        var tableIds = snapshot.Tables.Select(t => t.Id).ToHashSet();
        snapshot.Relationships.Should().OnlyContain(r => tableIds.Contains(r.DependencyId));
    }

    [Fact]
    public void Map_IsDeterministicSoOneDatabaseStateIsOneSetOfIds()
    {
        var first = Mapper.Map(InterchangeTestModel.Full());
        var second = Mapper.Map(InterchangeTestModel.Full());

        second.Tables.Select(t => t.Id).Should().Equal(first.Tables.Select(t => t.Id));
        second.Relationships.Select(r => r.DependencyId).Should().Equal(first.Relationships.Select(r => r.DependencyId));
        second.Routines.Select(r => r.Id).Should().Equal(first.Routines.Select(r => r.Id));
    }

    [Fact]
    public void MapNativeObjects_HoldsOnlyTheKindsWithNoStructuredHome()
    {
        var natives = Mapper.MapNativeObjects(InterchangeTestModel.Full());

        natives.Select(n => $"{n.ObjectKind}/{n.Name}").Should()
            .Equal(
                "customType/address",
                "customType/mood",
                "domain/positive_amount",
                "publication/pub_all",
                "subscription/sub_1");

        // Views, functions, triggers, sequences, extensions and policies are mapped structurally
        // and carry their own native text — repeating them here would give the import two sources
        // of truth for one object.
        var structuredKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "view", "materializedView", "function", "procedure", "trigger", "sequence",
            "extension", "policy",
        };

        natives.Should().NotContain(n => structuredKinds.Contains(n.ObjectKind));
    }

    [Fact]
    public void MapNativeObjects_PreservesTheTypeStatementAndTheSourceDialect()
    {
        var natives = Mapper.MapNativeObjects(InterchangeTestModel.Full());

        var mood = natives.Single(n => n.Name == "mood");
        mood.NativeSql.Should().Be("CREATE TYPE public.mood AS ENUM ('sad', 'happy')");
        mood.SourceDialect.Should().Be(Dialect);

        natives.Single(n => n.Name == "pub_all").Dependencies.Should().BeEmpty();
    }

    [Fact]
    public void Map_EmptyModelProducesAnEmptyButWellFormedSnapshot()
    {
        var snapshot = Mapper.Map(InterchangeTestModel.Empty());

        snapshot.Schemas.Should().BeEmpty();
        snapshot.Tables.Should().BeEmpty();
        snapshot.Views.Should().BeEmpty();
        snapshot.Routines.Should().BeEmpty();
        snapshot.Relationships.Should().BeEmpty();
        snapshot.Security!.Policies.Should().BeEmpty();
    }

    [Fact]
    public void Map_PartitionedTableKeepsItsKind()
    {
        var model = InterchangeTestModel.Empty() with
        {
            Tables = [PartitionedTable("events", "RANGE")],
        };

        var events = Mapper.Map(model).Tables.Single();

        events.TableKind.Should().Be(ETableKind.Partitioned);
        events.PartitionStrategy.Should().Be("RANGE");
        events.ParentId.Should().BeNull();
        events.PartitionBound.Should().BeNull();
    }

    [Fact]
    public void Map_PartitionCarriesItsParentIdAndBound()
    {
        var model = InterchangeTestModel.Empty() with
        {
            Tables =
            [
                PartitionedTable("measurements", "RANGE"),
                Partition(
                    "measurements_2026",
                    "public.measurements",
                    "FOR VALUES FROM ('2026-01-01') TO ('2027-01-01')"),

                // An INHERITS child has a parent and no bound, and the format tells the two apart by
                // exactly that, so the bound has to stay absent rather than be read as DEFAULT.
                Partition("measurements_legacy", "public.measurements", null),
            ],
        };

        var snapshot = Mapper.Map(model);
        var parent = snapshot.Tables.Single(t => t.Name == "measurements");

        var partition = snapshot.Tables.Single(t => t.Name == "measurements_2026");
        partition.ParentId.Should().Be(parent.Id);
        partition.PartitionBound.Should().Be("FOR VALUES FROM ('2026-01-01') TO ('2027-01-01')");
        partition.Notes.Should().BeEmpty();

        var inherited = snapshot.Tables.Single(t => t.Name == "measurements_legacy");
        inherited.ParentId.Should().Be(parent.Id);
        inherited.PartitionBound.Should().BeNull();
    }

    [Fact]
    public void Map_PartitionOfAnUncapturedParent_NamesNoIdAndRecordsTheLoss()
    {
        var model = InterchangeTestModel.Empty() with
        {
            Tables = [Partition("orders_2026", "public.orders", "FOR VALUES FROM ('2026') TO ('2027')")],
        };

        var child = Mapper.Map(model).Tables.Single();

        // An id standing for a table the catalog does not hold would hand the restore a statement it
        // cannot run, so the link is left out and the omission is written down.
        child.ParentId.Should().BeNull();

        var note = child.Notes.Single();
        note.Code.Should().Be("PartitionParentNotCaptured");
        note.Severity.Should().Be(EIssueSeverity.Warning);
    }

    [Fact]
    public void Map_OwnedSequence_RecordsThatTheFormatCannotCarryTheOwnershipLink()
    {
        var model = InterchangeTestModel.Empty() with
        {
            Sequences =
            [
                new Models.SequenceDefinition(
                    "public",
                    "orders_id_seq",
                    1,
                    1,
                    null,
                    null,
                    1,
                    false,
                    "bigint",
                    null,
                    OwnerTable: "public.orders",
                    OwnerColumn: "id",
                    IsIdentity: true),
            ],
        };

        var sequence = Mapper.Map(model).Sequences.Single();

        // The restore recreates this sequence standalone, so the catalog has to say that the link it
        // is losing was there — otherwise the import looks faithful and is not.
        sequence.Notes.Should().Contain(n => n.Code == "SequenceOwnershipNotCaptured");
        sequence.Notes.Should().Contain(n => n.Code == "SequenceStateNotCaptured");
    }

    [Fact]
    public void RoutineSignature_JoinsParameterTypesInLowercaseSoIdAndSourceObjectIdAgree() =>
        DatabaseModelMapper
            .RoutineSignature(InterchangeTestModel.Full().Functions.Single(f => f.Name == "add_customer"))
            .Should()
            .Be("character varying");

    /// <summary>A partitioned table standing on its own, with the given strategy.</summary>
    private static Models.TableDefinition PartitionedTable(string name, string strategy) =>
        new(
            "public",
            name,
            [],
            [],
            [],
            [],
            [],
            null,
            IsPartitioned: true,
            PartitionStrategy: strategy,
            ParentTable: null);

    /// <summary>
    /// A child table: the metadata reader reports its parent as one "schema.table" key, and a null
    /// bound is what separates an <c>INHERITS</c> child from a partition.
    /// </summary>
    private static Models.TableDefinition Partition(string name, string parentKey, string? bound) =>
        new(
            "public",
            name,
            [],
            [],
            [],
            [],
            [],
            null,
            IsPartitioned: false,
            PartitionStrategy: null,
            ParentTable: parentKey,
            PartitionBound: bound);
}
