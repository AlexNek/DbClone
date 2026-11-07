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
    public void Map_TriggerHandsTheEngineStatementTextAndTheStateItWasReportedIn()
    {
        var model = InterchangeTestModel.Full();
        var triggers = model.Triggers.Select(
            t => t.Name == "tr_audit" ? t with { EnabledState = "R" } : t).ToList();

        var snapshot = Mapper.Map(model with { Triggers = triggers });
        var audit = snapshot.Tables.Single(t => t.Name == "customers").Triggers.Single();

        // The statement text is what the trigger stage runs, because the structured fields name no
        // function to call. The enabled flag travels as the engine reported it rather than being
        // flattened to enabled-or-disabled: a state the restore has no statement for has to reach it as
        // itself, so the stage refuses it instead of creating a trigger that fires when the source's did
        // not. The other trigger in the same model reports no flag, and its state stays derived.
        audit.NativeDefinition!.Dialect.Should().Be(Dialect);
        audit.NativeDefinition.CapturedBy.Should().Be("pg_get_triggerdef");
        audit.NativeDefinition.Complete.Should().BeTrue();
        audit.NativeDefinition.Text.Should().StartWith("CREATE TRIGGER tr_audit BEFORE INSERT OR UPDATE");
        audit.EnabledState.Should().Be("R");
        snapshot.Tables.Single(t => t.Name == "orders").Triggers.Single()
            .EnabledState.Should().Be("D");
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

        natives.Select(n => $"{n.Kind}/{n.SchemaName}/{n.Name}").Should()
            .Equal(
                "CompositeType/public/address",
                "Domain/public/positive_amount",
                "Enum/public/mood",
                "Publication//pub_all",
                "Subscription//sub_1");

        // Views, functions, triggers, sequences, extensions and policies are mapped structurally
        // and carry their own native text — repeating them here would give the import two sources
        // of truth for one object.
        var structuredKinds = new HashSet<EObjectKind>
        {
            EObjectKind.View, EObjectKind.MaterializedView, EObjectKind.Function, EObjectKind.Procedure,
            EObjectKind.Trigger, EObjectKind.Sequence, EObjectKind.Extension, EObjectKind.Policy,
        };

        natives.Should().NotContain(n => structuredKinds.Contains(n.Kind));
    }

    [Fact]
    public void MapNativeObjects_IdentifyTheirObjectsFromTheSameGraphTheSectionsUse()
    {
        var model = InterchangeTestModel.Full();
        var natives = Mapper.MapNativeObjects(model);
        var graph = InterchangeIdGraph.Create(model, Dialect);

        var mood = natives.Single(n => n.Name == "mood");
        mood.Id.Should().Be(graph.Type("public", "mood"));
        mood.SourceObjectId.Should().Be("public.mood");

        // A publication belongs to the database rather than to a schema, so it is identified the way
        // an extension is: by its name alone.
        var publication = natives.Single(n => n.Name == "pub_all");
        publication.Id.Should().Be(graph.Publication("pub_all"));
        publication.SourceObjectId.Should().Be("pub_all");

        natives.Single(n => n.Name == "sub_1").Id.Should().Be(graph.Subscription("sub_1"));

        // One pg_type namespace per schema is what lets an enum, a composite and a domain share the
        // "type" token, so no two of these objects may land on one id.
        natives.Select(n => n.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void MapNativeObjects_CarriesTheStatementAndTheDialectItIsWrittenIn()
    {
        var natives = Mapper.MapNativeObjects(InterchangeTestModel.Full());

        var mood = natives.Single(n => n.Name == "mood");
        mood.NativeDefinition!.Text.Should().Be("CREATE TYPE public.mood AS ENUM ('sad', 'happy')");
        mood.NativeDefinition.Dialect.Should().Be(Dialect);
        mood.NativeDefinition.Complete.Should().BeTrue();

        natives.Single(n => n.Name == "address").NativeDefinition!.Text.Should()
            .Be("CREATE TYPE public.address AS (city text)");

        var domain = natives.Single(n => n.Name == "positive_amount");
        domain.NativeDefinition!.Text.Should()
            .Be("CREATE DOMAIN public.positive_amount AS numeric NOT NULL CHECK ((VALUE > 0))");

        // The text is whole for what the metadata model reports about the domain; a collation declared
        // on the base type is not among what it reports, and the note says so.
        domain.Notes.Should().ContainSingle().Which.Code.Should().Be("DomainCollationNotCaptured");

        natives.Single(n => n.Name == "pub_all").Dependencies.Should().BeEmpty();
    }

    [Fact]
    public void MapNativeObjects_QuotesNamesAndEscapesLabelsSoTheTextRestatesTheDeclaration()
    {
        var model = InterchangeTestModel.Empty() with
        {
            Enums = [new Models.EnumDefinition("Sales", "Mood", ["ok", "it's fine"], null)],
            CompositeTypes =
            [
                new Models.CompositeTypeDefinition(
                    "Sales",
                    "Address",
                    [new Models.ColumnDefinition("City", "text", 1, false, null, false, false, null, null)],
                    null),
            ],
            Domains =
            [
                new Models.DomainDefinition(
                    "Sales", "Amount", "numeric", "1", "CHECK ((VALUE > 0))", false, null),
            ],
        };

        var natives = Mapper.MapNativeObjects(model);

        // A name the source stored in mixed case comes back quoted, or the server would fold it to
        // lower case and create a different type; an apostrophe in a label is doubled, which is
        // escaping for a literal rather than quoting for a name.
        natives.Single(n => n.Kind == EObjectKind.Enum).NativeDefinition!.Text.Should()
            .Be("""CREATE TYPE "Sales"."Mood" AS ENUM ('ok', 'it''s fine')""");
        natives.Single(n => n.Kind == EObjectKind.CompositeType).NativeDefinition!.Text.Should()
            .Be("""CREATE TYPE "Sales"."Address" AS ("City" text NOT NULL)""");
        natives.Single(n => n.Kind == EObjectKind.Domain).NativeDefinition!.Text.Should()
            .Be("""CREATE DOMAIN "Sales"."Amount" AS numeric DEFAULT 1 NOT NULL CHECK ((VALUE > 0))""");
    }

    [Fact]
    public void MapNativeObjects_PublicationOfNothingSaysSoBecauseLeavingItOutMeansEverything()
    {
        var model = InterchangeTestModel.Empty() with
        {
            Publications = [new Models.PublicationDefinition("pub_none", false, [], null)],
        };

        var publication = Mapper.MapNativeObjects(model).Single();

        publication.NativeDefinition!.Text.Should().Be("CREATE PUBLICATION pub_none FOR NOTHING");

        // Which operations it publishes, and whether it publishes through a partitioned table's root,
        // are in no field of the metadata model.
        publication.NativeDefinition.Complete.Should().BeFalse();
        publication.Notes.Should().ContainSingle().Which.Code.Should().Be("PublicationSettingsNotCaptured");
    }

    [Fact]
    public void MapNativeObjects_SubscriptionKeepsNoStatementBecauseItsTextHoldsACredential()
    {
        var natives = Mapper.MapNativeObjects(InterchangeTestModel.Full());

        var subscription = natives.Single(n => n.Kind == EObjectKind.Subscription);

        subscription.NativeDefinition.Should().BeNull();
        subscription.Dependencies.Should().Equal("pub_all");
        subscription.Notes.Should().ContainSingle().Which.Code.Should().Be("CredentialOmitted");

        // A catalog is a file that gets copied, so no captured statement of any object may carry the
        // publisher's password.
        natives.Where(n => n.NativeDefinition is not null)
            .Select(n => n.NativeDefinition!.Text)
            .Should()
            .NotContain(text => text.Contains("fake_pw_123", StringComparison.Ordinal));
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
