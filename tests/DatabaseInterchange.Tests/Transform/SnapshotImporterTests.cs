using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DbAdapter.Definitions.Interchange;

using DatabaseInterchange.Format;
using DatabaseInterchange.Transform;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Xunit;

namespace DatabaseInterchange.Tests.Transform;

/// <summary>
/// Hermetic tests for the import pass: a catalog is written to a temp directory and imported into a
/// writer that records what it was asked to do. No connection is opened.
/// <para>
/// What the importer owns — and therefore what only it can get wrong — is the order the stages are
/// applied in, and the names behind the ids. A catalog object points at its parent by
/// <see cref="ObjectRef"/> alone, and an adapter may not open a catalog to look a ref up, so
/// resolving those refs into names is work the reader side has to finish before the objects cross the
/// boundary.
/// </para>
/// </summary>
public sealed class SnapshotImporterTests : IDisposable
{
    private static readonly ObjectRef PublicRef = ObjectRef.Schema("public");

    private static readonly ObjectRef ReportingRef = ObjectRef.Schema("reporting");

    private static readonly ObjectRef CustomersRef = ObjectRef.Table("public", "customers");

    private static readonly ObjectRef CustomersSeqRef =
        ObjectRef.Sequence("reporting", "customers_seq");

    private static readonly ObjectRef CustomersEmailIdxRef =
        ObjectRef.SubObject("index", "public", "customers", "customers_email_idx");

    private static readonly ObjectRef MoodTypeRef = ObjectRef.Type("public", "mood");

    /// <summary>
    /// The signature-prefixed bytes the container fixture stores: long enough to cross the threshold the
    /// catalog is written under, so the value leaves the row file.
    /// </summary>
    private static readonly byte[] PngValue = BuildPngValue();

    private readonly string _catalogPath = Path.Combine(
        Path.GetTempPath(),
        "DbInterchangeImportTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
        {
            Directory.Delete(_catalogPath, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_AppliesEveryStageInTheOrderTheContractDeclares()
    {
        // Arrange
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        // SchemaStage's order is a dependency order: routines come before views because a view may
        // call a function, and the constraints come after every table because a foreign key points
        // between tables that all have to exist first. An extension is installed after the schemas
        // because it is installed into one, and before everything else because the objects that
        // follow may name what it provides — the operator class of an index, for one.
        writer.Stages.Should().Equal(
            SchemaStage.Schemas,
            SchemaStage.Extensions,
            SchemaStage.Types,
            SchemaStage.Sequences,
            SchemaStage.Tables,
            SchemaStage.Routines,
            SchemaStage.Views,
            SchemaStage.Constraints,
            SchemaStage.Indexes,
            SchemaStage.Triggers,
            SchemaStage.Publications);

        // The keys travel with the table that owns them, so this stage is handed the tables again.
        writer.Objects[SchemaStage.Constraints].Should().Equal(writer.Objects[SchemaStage.Tables]);

        // So do the indexes, for a reason of the same kind: whether a captured index still has to be
        // created depends on the constraints recorded in the table that holds it, and an adapter may
        // not open the catalog to find them.
        writer.Objects[SchemaStage.Indexes].Should().Equal(writer.Objects[SchemaStage.Tables]);

        // A trigger needs nothing from its siblings once its table can be named, so the trigger objects
        // themselves are what travel to their stage.
        writer.Objects[SchemaStage.Triggers].Single().Should().BeAssignableTo<TriggerDefinition>();

        // The extension of the catalog's own section travels to the stage that installs it, which is the
        // stage that has to run before anything names what the extension provides.
        writer.Objects[SchemaStage.Extensions].Single().Should().BeAssignableTo<ExtensionDefinition>();

        // The native file's objects travel to the stage the routing table says creates their kind, and
        // only there: one of these objects is a type and one is not, so the two part company instead of
        // arriving at a single stage that would have to refuse one of them. The publication lands after
        // the triggers because it lists tables, which is the same numbering the types are placed by.
        writer.Objects[SchemaStage.Types].Cast<NativeObjectDefinition>().Select(n => n.Kind)
            .Should().Equal(EObjectKind.Enum);
        writer.Objects[SchemaStage.Publications].Cast<NativeObjectDefinition>().Select(n => n.Kind)
            .Should().Equal(EObjectKind.Publication);
    }

    [Fact]
    public async Task ImportAsync_CatalogHoldsAnObjectTheAdapterDoesNotCreate_StopsBeforeTheFirstStage()
    {
        // Arrange
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        var act = () => Import(writer, kind => kind != EObjectKind.Publication);

        // Assert
        // The refusal is made where the target is still untouched, and it names every object nothing can
        // create — an import that ran its earlier stages first would leave a partial target to explain
        // the one object it never created.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Publication *\"pub_all\"*nothing was executed*");
        writer.Stages.Should().BeEmpty();
        writer.RowTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_KindTheFormatHasNoStageFor_IsRefusedRatherThanGuessedAt()
    {
        // Arrange
        // A table arriving through the native file has a stage that creates tables and a routing table
        // that does not take that kind from this file, which is the catalog holding two sources of truth
        // for one object. Guessing a stage for it would let the same table be created twice.
        await WriteCatalog(nativeOverride: natives =>
            [.. natives, new NativeObjectDefinition { Kind = EObjectKind.Table, Name = "customers" }]);
        var writer = new RecordingWriter();

        // Act
        var act = () => Import(writer);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Table *\"customers\"*nothing was executed*");
        writer.Stages.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_RefusedTarget_StatesWhatItHoldsAndLeavesTheRequirementInTheDetails()
    {
        // Arrange — a target whose only content is an extension, which is what a database restored
        // from the same catalog keeps.
        const string reason = "The target database \"testdb\" already holds 1 extension.";
        await WriteCatalog();
        var writer = new RecordingWriter { Refusal = reason };

        // Act
        var result = await Import(writer);

        // Assert — the sentence is what the target holds and nothing else, because the UI turns it into
        // the question it asks before a restore and into the reason a refused restore reports. What a
        // restore requires of a target would read as an instruction in both, so it travels in Details.
        result.Success.Should().BeFalse();
        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.Code.Should().Be("RestoreRefused");
        issue.Message.Should().Be(reason);
        issue.Details.Should().Contain("empty database").And.Contain("separate act");
        writer.Stages.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_HandsTheWriterTheNamesBehindTheIds()
    {
        // Arrange
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        var sequence = (SequenceDefinition)writer.Objects[SchemaStage.Sequences].Single();
        sequence.Name.Should().Be("customers_seq");

        // The object carries a qualified-name reference (ObjectRef) that already contains the schema
        // name, so no separate name map is needed to resolve it.
        sequence.SchemaRef.ObjectName().Should().Be("reporting");
    }

    [Fact]
    public async Task ImportAsync_AddressesRowsByTheSchemaTheirTableLivesIn()
    {
        // Arrange
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        // A bare table name would land the rows in whichever schema the connection's search path
        // reached first, so the schema has to travel with the table.
        writer.RowTargets.Should().Equal(new TableRef("public", "customers"));
    }

    [Fact]
    public async Task ImportAsync_HandsTheRecordedColumnsToTheWriterSoPositionsAreNotGuessed()
    {
        // Arrange
        await WriteCatalogWithData();
        var writer = new RecordingWriter();

        // Act
        var result = await Import(writer);

        // Assert
        // A stored row is a list of text tokens and nothing in it says which column a token answers,
        // so the columns the catalog recorded have to travel with the rows.
        writer.RowColumns[new TableRef("public", "customers")]
            .Select(c => c.Name)
            .Should().Equal("id", "name");
        result.RowsImported.Should().Be(2);
    }

    [Fact]
    public async Task ImportAsync_AValueStoredInAContainer_ReachesTheWriterAsAPayloadAndNotAsRebuiltHex()
    {
        // Arrange
        await WriteCatalogWithContainerData();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        // Design §5/§6.9: the importer's job ends at handing the value over. Turning the range back into a
        // hex literal here would put the whole value in memory as text and hand the writer SQL to embed it
        // in — the exact failure part B exists to remove, so it is the importer half that has to be pinned.
        var value = writer.Rows.Single()[1];
        value.Binary.Should().NotBeNull("a container value travels as bytes");
        value.Value.Should().BeNull("and never as a hex token the writer would have to decode");
        value.Binary!.Length.Should().Be(PngValue.Length);
        (await ReadAllAsync(value.Binary)).Should().BeEquivalentTo(PngValue);
    }

    [Fact]
    public async Task ImportAsync_CountsTheTargetAgainstTheRowsTheCaptureWroteDown()
    {
        // Arrange
        await WriteCatalogWithData();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        // The expectation is the count the catalog's own table manifest states — written by the export —
        // and not a restatement of what the writer says it applied, or validation would check the
        // restore against itself.
        writer.Expectation.Should().NotBeNull();
        writer.Expectation!.TableRowCounts[new TableRef("public", "customers")].Should().Be(2);
    }

    [Fact]
    public async Task ImportAsync_CatalogHoldingNoDataForATable_ExpectsNoRowCountForIt()
    {
        // Arrange
        // The shared fixture writes the schema sections and no data folder, which is what an export run
        // without data produces.
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        writer.Expectation.Should().NotBeNull();
        writer.Expectation!.TableRowCounts.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_ExpectationNamesOnlyTheKindsTheCatalogHolds()
    {
        // Arrange
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        writer.Expectation.Should().NotBeNull();
        var counts = writer.Expectation!.ObjectCounts;
        counts[EObjectKind.Schema].Should().Be(2);
        counts[EObjectKind.Table].Should().Be(1);
        counts[EObjectKind.Enum].Should().Be(1);
        counts.Should().NotContainKey(EObjectKind.Procedure);
        counts.Should().NotContainKey(EObjectKind.MaterializedView);
    }

    [Fact]
    public async Task ImportAsync_ExpectationCountsAnIdentitySequencesAtTheColumnThatCreatesIt()
    {
        // Arrange
        await WriteCatalogWithIdentityColumn();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        // One sequence the restore writes itself, and one the identity column creates. The catalog's
        // own record of the identity's sequence adds nothing to the total, and a recorded sequence
        // whose table the catalog does not hold expects nothing on the target: both of those describe
        // the column, and counting them again would report a difference the restore does not have.
        writer.Expectation.Should().NotBeNull();
        writer.Expectation!.ObjectCounts[EObjectKind.Sequence].Should().Be(2);
    }

    [Fact]
    public async Task ImportAsync_StageThatFails_IsReportedAndTheLaterStagesStillRun()
    {
        // Arrange — the target refuses the step that creates the indexes, which is one part of the
        // structure and no fact at all about the steps that follow it.
        await WriteCatalog();
        var writer = new RecordingWriter();
        writer.FailingStages.Add(SchemaStage.Indexes);

        // Act
        var result = await Import(writer);

        // Assert — the walk went on past the failure, and the failure itself is what the user reads.
        writer.Stages.Should().Contain(SchemaStage.Triggers, "the steps after the failed one still run");
        result.Success.Should().BeTrue("everything the other steps create still landed");

        // The skipped figure counts the objects the restore applies and reports, and keys and indexes
        // are not among them: an index the target refused is not a table it never created. The loss is
        // still stated — in the tables the step was handed, not in a figure that belongs to no object.
        result.ObjectsSkipped.Should().Be(0);

        var issue = result.Issues.Should()
            .ContainSingle(i => i.Code == "RestoreStageFailed").Which;
        issue.Severity.Should().Be(EIssueSeverity.Error);
        issue.Message.Should().Contain("indexes").And.Contain("refused");
        issue.Message.Should().Contain($"over {writer.Objects[SchemaStage.Indexes].Count} table(s)");
    }

    [Fact]
    public async Task ImportAsync_StageOfCountedObjectsFails_ReportsTheCountItCost()
    {
        // Arrange — the target refuses the step that creates the sequences, which the restore's own
        // count of applied objects does include.
        await WriteCatalog();
        var writer = new RecordingWriter();
        writer.FailingStages.Add(SchemaStage.Sequences);

        // Act
        var result = await Import(writer);

        // Assert — the one sequence this catalog holds is reported as missing instead of counted as
        // applied, and the run is still a completed restore rather than a failure.
        result.Success.Should().BeTrue();
        result.ObjectsSkipped.Should().Be(1);

        var issue = result.Issues.Should()
            .ContainSingle(i => i.Code == "RestoreStageFailed").Which;
        issue.Message.Should().Contain("sequences").And.Contain("1 object(s)");
    }

    [Fact]
    public async Task ImportAsync_TableWhoseRowsAreRefused_IsReportedAndTheCheckStillRuns()
    {
        // Arrange — the target refuses the rows of the one table the catalog holds data for.
        await WriteCatalogWithData();
        var writer = new RecordingWriter();
        writer.FailingTables.Add(new TableRef("public", "customers"));

        // Act
        var result = await Import(writer);

        // Assert — the failure belongs to that table alone: the restore completes, states that none of
        // its rows arrived, and the check after the data runs all the same.
        result.Success.Should().BeTrue();
        result.RowsImported.Should().Be(0);
        result.Issues.Should()
            .ContainSingle(i => i.Code == "RestoreTableDataFailed")
            .Which.Message.Should().Contain("public.customers");

        // The table the target refused is still counted against the catalog's record of it. A count read
        // after the write would drop it — the write threw, so there would be nothing to read — and the
        // check would then find the restore agreeing with itself while the table sits empty.
        writer.Expectation.Should().NotBeNull("the check after the data still runs");
        writer.Expectation!.TableRowCounts[new TableRef("public", "customers")].Should().Be(2);
    }

    [Fact]
    public async Task ImportAsync_TablesStepFails_RowsAreNotAttemptedAndTheRestoreIsReportedAsFailed()
    {
        // Arrange — the step that creates the tables is the one nothing else can be built on.
        await WriteCatalogWithData();
        var writer = new RecordingWriter();
        writer.FailingStages.Add(SchemaStage.Tables);

        // Act
        var result = await Import(writer);

        // Assert — no table means no place for a row, said once instead of once per table, and the run
        // is a failed restore rather than a completed one with a note.
        writer.RowTargets.Should().BeEmpty();
        result.Success.Should().BeFalse("nothing can be built on a table that was not created");
        result.Issues.Select(issued => issued.Code)
            .Should().Contain("RestoreStageFailed").And.Contain("RestoreDataNotWritten");
    }

    [Fact]
    public async Task ImportAsync_CheckAfterTheDataFails_IsReportedWithoutReplacingTheRestore()
    {
        // Arrange — everything lands and the check on it cannot read the target.
        await WriteCatalogWithData();
        var writer = new RecordingWriter { ValidationFails = true };

        // Act
        var result = await Import(writer);

        // Assert — a restore is not undone by its own check: it completes, no report is invented, and
        // the user is told that the check did not run.
        result.Success.Should().BeTrue();
        result.ValidationReport.Should().BeNull();
        result.Issues.Should()
            .ContainSingle(i => i.Code == "RestoreValidationNotRun")
            .Which.Severity.Should().Be(EIssueSeverity.Warning);
    }

    [Fact]
    public async Task ImportAsync_ExpectationNamesEverySchemaTheCatalogRecords()
    {
        // Arrange
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        // The names travel beside the counts: an adapter answers a schema it creates apart from one
        // its own engine already keeps, and a total cannot tell the two apart.
        writer.Expectation.Should().NotBeNull();
        writer.Expectation!.SchemaNames.Should().Equal("public", "reporting");
    }

    [Fact]
    public async Task ImportAsync_TableWhoseSchemaTheCatalogDoesNotName_StopsBeforeReadingRows()
    {
        // Arrange
        await WriteCatalog(schemalessTable: true);
        var writer = new RecordingWriter();

        // Act
        var act = () => Import(writer);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*customers*");
        writer.RowTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_AppliesAStageEvenWhenTheCatalogHoldsNoneOfThatKind()
    {
        // Arrange
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        // An empty stage is passed through rather than skipped: what emptiness means is the adapter's
        // decision, not the importer's.
        writer.Objects[SchemaStage.Views].Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_RowsFollowTheKeys_SoATableIsWrittenBeforeTheRowsThatNameIt()
    {
        // Arrange — the catalog holds a child whose name sorts before the table its key points at, which
        // is the alphabetical order the section is written in.
        await WriteCatalogWithChildBeforeParent();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        // The constraints stage has already created the foreign key and a key is checked on every row
        // inserted after it, so the parent's rows have to be there first. The order of the section is no
        // load order at all: walking it as written refuses the child's rows, and theirs alone.
        writer.RowTargets.Should().Equal(
            new TableRef("public", "z_accounts"),
            new TableRef("public", "a_notes"));
    }

    private static async Task WriteCatalog(
        string catalogPath,
        bool schemalessTable = false,
        Func<IReadOnlyList<NativeObjectDefinition>, IReadOnlyList<NativeObjectDefinition>>? nativeOverride =
            null)
    {
        var schemas = new List<SchemaDefinition>
        {
            new() { Ref = PublicRef, Name = "public" },
            new() { Ref = ReportingRef, Name = "reporting" },
        };

        var tables = new List<TableDefinition>
        {
            new()
            {
                Ref = CustomersRef,
                Name = "customers",
                SchemaRef = schemalessTable
                    ? ObjectRef.Schema("")
                    : PublicRef,

                // An index and a trigger belong to the table that owns them, and the stages that
                // create them take those objects rather than the table, so the fixture carries one of
                // each to pin where they are handed over.
                Indexes =
                [
                    new IndexDefinition
                    {
                        Ref = CustomersEmailIdxRef,
                        Name = "customers_email_idx",
                        TableRef = CustomersRef,
                    },
                ],
                Triggers =
                [
                    new TriggerDefinition
                    {
                        Ref = ObjectRef.SubObject("trigger", "public", "customers", "customers_touch"),
                        Name = "customers_touch",
                        TableRef = CustomersRef,
                    },
                ],
            },
        };

        var sequences = new List<SequenceDefinition>
        {
            new() { Ref = CustomersSeqRef, Name = "customers_seq", SchemaRef = ReportingRef },
        };

        // The kinds the structured sections have no home for go to the catalog's native file, named after
        // the source dialect the manifest records. One of these is a type and one is not, which is what
        // lets a test see the file split between two stages rather than handed to one whole.
        var natives = new List<NativeObjectDefinition>
        {
            new()
            {
                Ref = MoodTypeRef,
                Kind = EObjectKind.Enum,
                SchemaName = "public",
                Name = "mood",
                NativeDefinition = new NativeDefinition(
                    "postgresql", "CREATE TYPE public.mood AS ENUM ('sad', 'happy')"),
            },
            new()
            {
                Kind = EObjectKind.Publication,
                Name = "pub_all",
                NativeDefinition = new NativeDefinition(
                    "postgresql", "CREATE PUBLICATION pub_all FOR ALL TABLES"),
            },
        };

        await using var catalog = await CatalogWriter.CreateAsync(
            catalogPath,
            producer: "DbClone-test",
            sourceDialect: "postgresql");

        await catalog.WriteSectionAsync("schemas", schemas);

        // An extension is captured in a section of its own rather than in the native file: the export
        // records its name, schema and version, and the restore installs it before anything names what
        // it provides.
        await catalog.WriteSectionAsync(
            "extensions",
            new List<ExtensionDefinition>
            {
                new() { Ref = ObjectRef.Extension("pg_trgm"), Name = "pg_trgm", Schema = "public" },
            });

        await catalog.WriteSectionAsync("tables", tables);
        await catalog.WriteSectionAsync("sequences", sequences);
        await catalog.WriteNativeObjectsAsync(
            nativeOverride is null ? natives : nativeOverride(natives));
        await catalog.CompleteAsync();
    }

    private Task WriteCatalog(
        bool schemalessTable = false,
        Func<IReadOnlyList<NativeObjectDefinition>, IReadOnlyList<NativeObjectDefinition>>? nativeOverride =
            null) =>
        WriteCatalog(_catalogPath, schemalessTable, nativeOverride);

    /// <summary>
    /// A catalog holding two related tables in the alphabetical order the section is written in: the
    /// child <c>a_notes</c> and the <c>z_accounts</c> its one key points at.
    /// </summary>
    private async Task WriteCatalogWithChildBeforeParent()
    {
        var parentRef = ObjectRef.Table("public", "z_accounts");
        var childRef = ObjectRef.Table("public", "a_notes");

        var parent = new TableDefinition
        {
            Ref = parentRef,
            Name = "z_accounts",
            SchemaRef = PublicRef,
            Columns = [IntegerColumn("z_accounts", parentRef, "id")],
        };

        var child = new TableDefinition
        {
            Ref = childRef,
            Name = "a_notes",
            SchemaRef = PublicRef,
            Columns = [IntegerColumn("a_notes", childRef, "account_id")],
            ForeignKeys =
            [
                new ForeignKeyDefinition
                {
                    Ref = ObjectRef.SubObject(
                        "foreignkey", "public", "a_notes", "a_notes_account_fkey"),
                    Name = "a_notes_account_fkey",
                    TableRef = childRef,
                    Columns = ["account_id"],
                    ReferencedTableRef = parentRef,
                    ReferencedColumns = ["id"],
                },
            ],
        };

        await using var catalog = await CatalogWriter.CreateAsync(
            _catalogPath,
            producer: "DbClone-test",
            sourceDialect: "postgresql");

        await catalog.WriteSectionAsync(
            "schemas",
            new List<SchemaDefinition> { new() { Ref = PublicRef, Name = "public" } });
        await catalog.WriteSectionAsync("tables", new List<TableDefinition> { child, parent });
        await catalog.CompleteAsync();
    }

    private static ColumnDefinition IntegerColumn(string table, ObjectRef tableRef, string name) =>
        new()
        {
            Ref = ObjectRef.Column("public", table, name),
            Name = name,
            TableRef = tableRef,
            Ordinal = 1,
            NativeType = new NativeType("integer"),
        };

    /// <summary>
    /// A catalog that holds an identity column, the sequence that column created, a sequence the
    /// restore writes itself, and a sequence whose table the catalog does not hold at all — the four
    /// facts a count of applied sequences has to tell apart.
    /// </summary>
    private async Task WriteCatalogWithIdentityColumn()
    {
        var customers = new TableDefinition
        {
            Ref = CustomersRef,
            Name = "customers",
            SchemaRef = PublicRef,
            Columns =
            [
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "customers", "id"),
                    Name = "id",
                    TableRef = CustomersRef,
                    Ordinal = 1,
                    NativeType = new NativeType("integer"),
                    Identity = "yes",
                },
            ],
        };

        var sequences = new List<SequenceDefinition>
        {
            new()
            {
                Ref = ObjectRef.Sequence("public", "customers_Id_seq"),
                Name = "customers_Id_seq",
                SchemaRef = PublicRef,
                IsIdentity = true,
            },
            new()
            {
                Ref = ObjectRef.Sequence("public", "audit_seq"),
                Name = "audit_seq",
                SchemaRef = PublicRef,
            },
            new()
            {
                Ref = ObjectRef.Sequence("public", "dropped_Id_seq"),
                Name = "dropped_Id_seq",
                SchemaRef = PublicRef,
                IsIdentity = true,
            },
        };

        await using var catalog = await CatalogWriter.CreateAsync(
            _catalogPath,
            producer: "DbClone-test",
            sourceDialect: "postgresql");

        await catalog.WriteSectionAsync(
            "schemas",
            new List<SchemaDefinition> { new() { Ref = PublicRef, Name = "public" } });
        await catalog.WriteSectionAsync("tables", new List<TableDefinition> { customers });
        await catalog.WriteSectionAsync("sequences", sequences);
        await catalog.CompleteAsync();
    }

    /// <summary>
    /// A catalog that holds a table with two columns and two rows of data, which is what lets a test
    /// watch the row pass instead of only the address it was handed.
    /// </summary>
    private async Task WriteCatalogWithData()
    {
        var customers = new TableDefinition
        {
            Ref = CustomersRef,
            Name = "customers",
            SchemaRef = PublicRef,
            Columns =
            [
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "customers", "id"),
                    Name = "id",
                    TableRef = CustomersRef,
                    Ordinal = 1,
                    NativeType = new NativeType("integer"),
                },
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "customers", "name"),
                    Name = "name",
                    TableRef = CustomersRef,
                    Ordinal = 2,
                    NativeType = new NativeType("text"),
                },
            ],
        };

        await using var catalog = await CatalogWriter.CreateAsync(
            _catalogPath,
            producer: "DbClone-test",
            sourceDialect: "postgresql");

        await catalog.WriteSectionAsync(
            "schemas",
            new List<SchemaDefinition> { new() { Ref = PublicRef, Name = "public" } });
        await catalog.WriteSectionAsync("tables", new List<TableDefinition> { customers });
        await catalog.WriteTableAsync(customers, "public", CustomerRows());
        await catalog.CompleteAsync();
    }

    /// <summary>
    /// A catalog whose data folder holds one table with a <see cref="ELogicalType.Binary"/> column and one
    /// value large enough to have earned a container, which is what lets a test watch a payload cross the
    /// writer boundary rather than only the row count.
    /// </summary>
    private async Task WriteCatalogWithContainerData()
    {
        var tableRef = ObjectRef.Table("public", "images");
        var images = new TableDefinition
        {
            Ref = tableRef,
            Name = "images",
            SchemaRef = PublicRef,
            Columns =
            [
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "images", "images_id"),
                    Name = "id",
                    TableRef = tableRef,
                    Ordinal = 1,
                    LogicalType = ELogicalType.Integer32,
                    NativeType = new NativeType("integer"),
                },
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "images", "images_data"),
                    Name = "data",
                    TableRef = tableRef,
                    Ordinal = 2,
                    LogicalType = ELogicalType.Binary,
                    NativeType = new NativeType("bytea"),
                },
            ],
        };

        await using var catalog = await CatalogWriter.CreateAsync(
            _catalogPath,
            producer: "DbClone-test",
            sourceDialect: "postgresql");

        await catalog.WriteSectionAsync(
            "schemas",
            new List<SchemaDefinition> { new() { Ref = PublicRef, Name = "public" } });
        await catalog.WriteSectionAsync("tables", new List<TableDefinition> { images });
        await catalog.WriteTableAsync(
            images,
            "public",
            ContainerRows(),
            new ExportOptions());
        await catalog.CompleteAsync();
    }

    private static async IAsyncEnumerable<DataValue[]> ContainerRows()
    {
        yield return new[]
        {
            DataValue.FromText(ELogicalType.Integer32, "1"),
            DataValue.FromText(ELogicalType.Binary, "\\x" + Convert.ToHexString(PngValue)),
        };

        await Task.CompletedTask;
    }

    private static byte[] BuildPngValue()
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var bytes = new byte[64];
        signature.CopyTo(bytes, 0);
        return bytes;
    }

    private static async Task<byte[]> ReadAllAsync(IBinaryPayload payload)
    {
        await using var stream = payload.OpenRead();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// The rows the catalog's data folder holds for <c>public.customers</c>: positional text, one token
    /// per recorded column, in the order the columns were recorded.
    /// </summary>
    private static async IAsyncEnumerable<DataValue[]> CustomerRows()
    {
        yield return new[]
        {
            DataValue.FromText(ELogicalType.Integer32, "1"),
            DataValue.FromText(ELogicalType.String, "ada"),
        };
        yield return new[]
        {
            DataValue.FromText(ELogicalType.Integer32, "2"),
            DataValue.Null(ELogicalType.String),
        };

        await Task.CompletedTask;
    }

    private async Task<ImportResult> Import(
        RecordingWriter writer,
        Func<EObjectKind, bool>? canCreate = null)
    {
        var adapter = Substitute.For<IDatabaseAdapter>();
        var capabilities = Substitute.For<IDatabaseCapabilities>();

        // The importer routes the native file by the answer the adapter gives here, so the double states
        // it. Everything the routing places in a stage is creatable unless the test says otherwise, which
        // leaves the order tests to watch the whole walk.
        capabilities.CanCreate(Arg.Any<EObjectKind>()).Returns(
            call => canCreate?.Invoke(call.Arg<EObjectKind>()) ?? true);
        adapter.Capabilities.Returns(capabilities);
        adapter.OpenWriter(Arg.Any<DatabaseTarget>(), Arg.Any<ImportOptions>()).Returns(writer);

        var importer = new SnapshotImporter(adapter, NullLogger<SnapshotImporter>.Instance);

        return await importer.ImportAsync(new ImportRequest
        {
            CatalogDirectory = _catalogPath,
            Target = new DatabaseTarget(
                "postgresql",
                "Host=test.example.com;Database=testdb;Username=test_user;Password=fake_pw_123"),
        });
    }

    /// <summary>
    /// A writer that records the calls instead of executing them and never fails, so a test sees the
    /// importer's decisions rather than a database's.
    /// </summary>
    private sealed class RecordingWriter : IDatabaseWriter
    {
        public List<SchemaStage> Stages { get; } = [];

        public Dictionary<SchemaStage, IReadOnlyList<ISchemaObject>> Objects { get; } = [];



        public List<TableRef> RowTargets { get; } = [];

        /// <summary>
        /// Every value the importer handed over, in row order. A test that only reads the counts cannot see
        /// what shape a value arrived in, which is what the payload rules turn on.
        /// </summary>
        public List<DataValue[]> Rows { get; } = [];

        public Dictionary<TableRef, IReadOnlyList<ColumnDefinition>> RowColumns { get; } = [];

        public TargetExpectation? Expectation { get; private set; }

        /// <summary>
        /// The stages this double refuses, so a test can watch what one failure costs the rest of the
        /// restore rather than only what the successful walk looks like.
        /// </summary>
        public HashSet<SchemaStage> FailingStages { get; } = [];

        /// <summary>The tables whose rows this double refuses instead of writing them.</summary>
        public HashSet<TableRef> FailingTables { get; } = [];

        /// <summary>Whether the check after the data fails instead of answering.</summary>
        public bool ValidationFails { get; set; }

        /// <summary>
        /// The sentence this double refuses the target with, or <see langword="null"/> to accept it.
        /// </summary>
        public string? Refusal { get; set; }

        public IDatabaseAdapter Adapter { get; } = Substitute.For<IDatabaseAdapter>();

        public ICapabilityDetector CapabilityDetector => null!;

        public Task EnsureEmptyTargetAsync(CancellationToken ct) =>
            Refusal is null
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException(Refusal));

        public Task ApplySchemaAsync(
            SchemaStage stage,
            IReadOnlyList<ISchemaObject> objects,
            CancellationToken ct)
        {
            Stages.Add(stage);
            Objects[stage] = objects;

            if (FailingStages.Contains(stage))
            {
                throw new InvalidOperationException($"the {stage} stage was refused by the target");
            }

            return Task.CompletedTask;
        }

        public async Task<long> WriteTableRowsAsync(
            TableRef table,
            IReadOnlyList<ColumnDefinition> columns,
            IAsyncEnumerable<DataValue[]> rows,
            CancellationToken ct)
        {
            RowTargets.Add(table);
            RowColumns[table] = columns;

            if (FailingTables.Contains(table))
            {
                throw new InvalidOperationException($"the rows of {table} were refused by the target");
            }

            // The rows are counted as they pass, the way a writer must count them to report what it
            // applied. What the test reads afterwards is the importer's accumulation of those counts.
            var applied = 0L;
            await foreach (var row in rows.WithCancellation(ct))
            {
                Rows.Add(row);
                applied++;
            }

            return applied;
        }

        public Task ApplySecurityAsync(
            ESecurityStage stage,
            SecuritySection security,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task<ImportValidationReport> ValidateAsync(
            TargetExpectation expectation,
            CancellationToken ct)
        {
            Expectation = expectation;

            if (ValidationFails)
            {
                throw new InvalidOperationException("the check could not read the target");
            }

            return Task.FromResult(new ImportValidationReport());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
