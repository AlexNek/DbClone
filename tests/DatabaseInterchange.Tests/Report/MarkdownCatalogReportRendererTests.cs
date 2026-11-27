using System.Text.RegularExpressions;

using CanonicalModel.Interchange;

using DatabaseInterchange.Report;
using DatabaseInterchange.Script;

using DbAdapter.Definitions;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Report;

/// <summary>
/// The document's own half of §6: the identity lines, the three byte measures, the two listings and the
/// metadata groups, each pinned to the exact text it must print.
/// </summary>
public sealed class MarkdownCatalogReportRendererTests
{
    private const string IdentityScope =
        "Statistics describe the objects included in this catalog; objects excluded by the selection are not counted.";

    [Fact]
    public void Render_PrintsTheIdentityLinesOfTheCatalog()
    {
        // Act
        var document = ReportModel.Document(TwoTables());

        // Assert (§6) — the five header lines, in order, saying what the numbers describe.
        var lines = Lines(document);
        lines[0].Should().Be("# Catalog summary — testdb");
        lines[1].Should().Be("Produced by DbClone-test · exported 2026-01-02T03:04:05Z");
        lines[2].Should().Be(
            "Format version 1 · source dialect postgresql · consistency: consistent snapshot");
        lines[3].Should().Be(IdentityScope);
        lines[4].Should().Be(
            "Sizes are exact bytes; prose sizes use binary units (KiB, MiB, GiB) on 1024 divisors.");
    }

    [Fact]
    public void Render_PrintsTheGlobalCountsAndTheThreeByteMeasuresAsSeparateColumns()
    {
        // Act
        var document = ReportModel.Document(TwoTables());

        // Assert (FR2, FR3) — every captured kind is its own column, and the byte measures are three
        // columns that differ by what they add up, not by rounding.
        Section(document, "## Global statistics").Should().Equal(
            "| schemas | tables | columns | views | matviews | routines | sequences | extensions | foreign keys | view references | roles | grants | policies |",
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |",
            "| 1 | 2 | 4 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |");
        Section(document, "## Data totals").Should().Equal(
            "| rows | chunk files | row bytes | blob files | blob bytes | data bytes | metadata bytes | catalog bytes | empty tables | largest table |",
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |",
            "| 20 | 2 | 240 | 0 | 0 | 240 | 0 | 240 | 0 | public.a (120) |",
            "Top 10 tables hold 100 % of the data bytes (240 of 240)");
    }

    [Fact]
    public void Render_PrintsOneRowPerSchemaAndPerTableInOrdinalOrder()
    {
        // Act
        var document = ReportModel.Document(TwoTables());

        // Assert (FR4) — the listings state the same figures the totals row sums.
        Section(document, "## Statistics per schema").Should().Equal(
            "| schema | tables | rows | data bytes | % of catalog bytes |",
            "| --- | --- | --- | --- | --- |",
            "| public | 2 | 20 | 240 | 100 |");
        Section(document, "## Statistics per table").Should().Equal(
            "| table | columns | rows | chunks | row bytes | blobs | blob bytes | PK | FK out | FK in | row order |",
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |",
            "| public.a | 2 | 10 | 1 | 120 | 0 | 0 | yes | 0 | 0 | primary key |",
            "| public.b | 2 | 10 | 1 | 120 | 0 | 0 | yes | 0 | 0 | primary key |");
    }

    [Fact]
    public void Render_PrintsEveryMetadataFileAndTheGroupTotalAndTheRootFilesRow()
    {
        // Arrange
        var snapshot = TwoTables();
        var statistics = ReportModel.Statistics(snapshot, metadataFiles:
        [
            ("schema/tables.json", 100L),
            ("native/postgresql.json", 70L),
            ("data/public/a/table.json", 30L),
        ], rootJsonBytes: 11);

        // Act
        var document = ReportModel.Document(snapshot, statistics);

        // Assert (FR5, D3) — the two root files have no per-file figure, so they read as one stated row.
        Section(document, "## Metadata files").Should().Equal(
            "| schema sections | bytes |",
            "| --- | --- |",
            "| schema/tables.json | 100 |",
            "| total | 100 |",
            "| engine native | bytes |",
            "| --- | --- |",
            "| native/postgresql.json | 70 |",
            "| total | 70 |",
            "| catalog and per-table metadata | bytes |",
            "| --- | --- |",
            "| data/public/a/table.json | 30 |",
            $"| {ReportText.RootMetadataFiles} | 11 |",
            "| total | 41 |");
    }

    [Fact]
    public void Render_PrintsARowForEveryOneOfFiveThousandTables()
    {
        // Arrange
        var snapshot = ReportModel.WideSchema(5000);

        // Act
        var document = ReportModel.Document(snapshot);

        // Assert (FR10, FR7b) — a table named in no line would be a table the catalog lost.
        Section(document, "## Statistics per table")
            .Count(row => row.StartsWith("| public.table_", StringComparison.Ordinal))
            .Should().Be(5000);
    }

    [Fact]
    public void Render_ForAnEmptyCatalog_PrintsTheHeaderAndNoneForEverySection()
    {
        // Act
        var document = ReportModel.Document(ReportModel.Snapshot());

        // Assert (FR10, AC7) — an empty catalog is a readable document, not a crash and not a blank file.
        document.Should().StartWith("# Catalog summary — testdb\n");
        Section(document, "## Statistics per schema").Should().Equal(ReportText.None);
        Section(document, "## Statistics per table").Should().Equal(ReportText.None);
        Section(document, "## Metadata files").Should().Equal(ReportText.None);
        Section(document, "## Catalog observations").Should().Contain("nothing unusual in this catalog");
    }

    [Fact]
    public void Render_ForASchemaOnlyStore_SaysNoDataCapturedInsteadOfPrintingZeros()
    {
        // Arrange — the capture wrote schema and no rows at all.
        var snapshot = TwoTables();
        var document = ReportModel.Document(snapshot, ReportModel.Statistics(snapshot, written: []));

        // Assert (FR10, AC7) — zeros would claim the source tables were empty, which was never measured.
        Section(document, "## Data totals").Should().Equal(
            "no data captured — this store wrote no table data",
            "| metadata bytes | catalog bytes |",
            "| --- | --- |",
            "| 0 | 0 |");
        Section(document, "## Statistics per table").Should().Equal(
            "| table | columns | rows | chunks | row bytes | blobs | blob bytes | PK | FK out | FK in | row order |",
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |",
            "| public.a | 2 | 0 | 0 | 0 | 0 | 0 | yes | 0 | 0 | primary key |",
            "| public.b | 2 | 0 | 0 | 0 | 0 | 0 | yes | 0 | 0 | primary key |");
    }

    [Fact]
    public void Render_NamesTheExcludedTablesInTheScopeLine()
    {
        // Act (FR17)
        var withExclusions = ReportModel.Document(
            TwoTables(), ReportModel.Statistics(TwoTables(), excludedTables: 3));

        // Assert — the scope line is always present and states the selection's part in the counts.
        Lines(withExclusions)[3].Should().Be(
            "Statistics describe the objects included in this catalog; objects excluded by the selection " +
            "(3 tables) are not counted.");
        Lines(ReportModel.Document(TwoTables()))[3].Should().Be(IdentityScope);
    }

    [Fact]
    public void Render_NeverGroupsADigitAndNeverPrintsAFractionalPercentage()
    {
        // Arrange — counts large enough for a culture-aware format to insert separators.
        var snapshot = ReportModel.Snapshot(ReportModel.KeyedTable("public", "events"));
        var document = ReportModel.Document(snapshot, ReportModel.Statistics(
            snapshot, [ReportModel.Written("public", "events", rows: 1234567, chunks: 1234, rowBytes: 9876543210)]));

        // Assert (D9.1)
        document.Should().Contain("1234567").And.Contain("9876543210");
        Matches(document, @"\d[,\u00A0\u202F\u2009]\d").Should().BeEmpty();
        Matches(document, @"\d+\.\d+ %").Should().BeEmpty();

        // The share column of each schema row is a whole number.
        List<string> shares = [.. Section(document, "## Statistics per schema").Skip(2)
            .Select(row => Cells(row)[^2])];
        shares.Should().NotBeEmpty();
        shares.Should().OnlyContain(share => Regex.IsMatch(share, @"^\d+$"));
    }

    [Fact]
    public void Render_NamesNoCredentialAndStillNamesTheObjectThatCarriesItsName()
    {
        // Arrange — a catalog whose captured objects mention a host, a role and a secret in the text the
        // source stores beside their names, and a column whose name is itself about credentials.
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "users"),
            ReportModel.Table("public", "credentials",
                ReportModel.Column("public", "credentials", "id", nullable: false),
                ReportModel.Column("public", "credentials", "password_hash", nullable: false)) with
            {
                PrimaryKey = ReportModel.PrimaryKey("public", "credentials", "id"),
                ForeignKeys =
                [
                    ReportModel.Reference(
                        "public", "credentials", "fk_credentials_user", "public", "users",
                        ["password_hash"], ["id"]),
                ],
            },
            ReportModel.KeyedTable("public", "sessions")) with
        {
            Views =
            [
                new ViewDefinition
                {
                    Ref = ObjectRef.View("public", "live_users"),
                    Name = "live_users",
                    SchemaRef = ObjectRef.Schema("public"),
                    NativeDefinition = new NativeDefinition(
                        "postgresql",
                        "CREATE VIEW ... WHERE owner = 'test_user' " +
                        "AND dblink('Host=test.example.com;User=test_user;Password=fake-secret-value')"),
                },
            ],
            Security = new SecuritySection
            {
                Roles = [new RoleDefinition { Ref = ObjectRef.Role("test_user"), Name = "test_user" }],
                Grants =
                [
                    new GrantDefinition
                    {
                        Ref = ObjectRef.Grant("test_user-public.users"),
                        Name = "test_user-public.users",
                        GranteeRef = ObjectRef.Role("test_user"),
                        TargetRef = ObjectRef.Table("public", "users"),
                        Privileges = ["SELECT"],
                    },
                ],
                Policies = [],
            },
        };

        // Act
        var document = ReportModel.Document(snapshot);

        // Assert (FR9, AC8) — the report prints object names, and a name that happens to be about
        // credentials is data the report exists to print.
        document.Should().NotContain("test.example.com");
        document.Should().NotContain("fake-secret-value");
        document.Should().NotContain("Host=");
        document.Should().NotContain("test_user");
        document.Should().Contain("password_hash");

        // The report states catalog-relative paths only, so no folder of the source machine appears.
        document.Should().NotContain(@"C:\").And.NotContain("/home/").And.NotContain("/Users/");
    }

    [Fact]
    public void Render_WithoutTheScriptPass_PrintsNoScriptsSection()
    {
        // Act
        var document = ReportModel.Document(TwoTables());

        // Assert (D9) — a section saying "nothing" would only raise a question nobody asked.
        document.Should().NotContain("## SQL scripts");
    }

    [Fact]
    public void Render_PrintsOneScriptsRowPerFileAndWhatTheFilesAreNot()
    {
        // Arrange
        var scripts = new ScriptResult
        {
            SchemaPath = "sql/schema.sql",
            SchemaBytes = 120,
            SchemaStatements = 7,
            DataFiles = [new ScriptDataFile("sql/data.sql", 340, 9, 20, null)],
            Rows = 20,
        };

        // Act
        var document = ReportModel.Document(TwoTables(), scripts: scripts);

        // Assert (FR7, D9, D10) — a file the pass wrote is a fact; the row states its size and what it
        // holds, and the S1 layout stays the two-row table it was before a family existed.
        Section(document, "## SQL scripts").Should().Equal(
            "| file | bytes | statements | rows |",
            "| --- | --- | --- | --- |",
            "| sql/schema.sql | 120 | 7 | - |",
            "| sql/data.sql | 340 | 9 | 20 |",
            "Every statement in these files may be run again: the second run creates no object and adds no row.",
            "Foreign keys are added unvalidated and validated at the end of `sql/data.sql`.",
            "The per-file budget is 0, so the rows are written in one file.",
            "Load it with `psql -f sql/schema.sql`, then `psql -f sql/data.sql`.",
            "A table that already exists is left as it is — no column, key or check is reconciled.",
            "A sequence keeps the position its own declaration gives it — a catalog records no current value.",
            "No role, grant or policy is written. Not a substitute for Restore, which also verifies the catalog.",
            "These files are not part of the catalog bytes above: they describe the catalog, they do not belong to it.");
    }

    [Fact]
    public void Render_ForAFamily_PrintsEveryFileAndLoadsThroughTheDriver()
    {
        // Arrange — a catalog whose largest table was split, so the pass wrote numbered files and a driver.
        var scripts = new ScriptResult
        {
            SchemaPath = "sql/schema.sql",
            SchemaBytes = 31_190,
            SchemaStatements = 153,
            DataFiles =
            [
                new ScriptDataFile("sql/data/0001.sql", 204_800, 120, 2_000, "public.big"),
                new ScriptDataFile("sql/data/0002.sql", 204_800, 120, 2_000, "public.big"),
                new ScriptDataFile("sql/data/0003.sql", 90_000, 41, 1_387, "public.big"),
            ],
            LoadPath = "sql/load.psql",
            Profile = EScriptProfile.Giants,
            BudgetBytes = 204_800,
            LargestTable = new TableRef("public", "big"),
            LargestTableBytes = 4_194_304,
            Rows = 5_387,
        };

        // Act
        var document = ReportModel.Document(TwoTables(), scripts: scripts);

        // Assert (FR7, FR10, D10) — one row per file, the tail named by the file that carries it, the
        // profile stated with the number it reasoned with, and the one command that loads them all.
        Section(document, "## SQL scripts").Should().Equal(
            "| file | bytes | statements | rows |",
            "| --- | --- | --- | --- |",
            "| sql/schema.sql | 31190 | 153 | - |",
            "| sql/data/0001.sql | 204800 | 120 | 2000 |",
            "| sql/data/0002.sql | 204800 | 120 | 2000 |",
            "| sql/data/0003.sql | 90000 | 41 | 1387 |",
            "Every statement in these files may be run again: the second run creates no object and adds no row.",
            "Foreign keys are added unvalidated and validated at the end of `sql/data/0003.sql`.",
            "The largest table, `public.big` (4.0 MiB), is larger than the 200.0 KiB per-file budget, so it is "
            + "written in 3 parts.",
            "Load it with `psql -f sql/load.psql`, which includes the files in the order they run.",
            "A table that already exists is left as it is — no column, key or check is reconciled.",
            "A sequence keeps the position its own declaration gives it — a catalog records no current value.",
            "No role, grant or policy is written. Not a substitute for Restore, which also verifies the catalog.",
            "These files are not part of the catalog bytes above: they describe the catalog, they do not belong to it.");
    }

    [Fact]
    public void Render_ForAWholeCatalogThatDoesNotFit_StatesTheBudgetAndThePackedFiles()
    {
        // Arrange — every table is within the budget, but together they are not (D2's third profile).
        var scripts = new ScriptResult
        {
            SchemaPath = "sql/schema.sql",
            SchemaBytes = 120,
            SchemaStatements = 7,
            DataFiles =
            [
                new ScriptDataFile("sql/data/0001.sql", 200_000, 40, 500, null),
                new ScriptDataFile("sql/data/0002.sql", 150_000, 30, 400, null),
            ],
            LoadPath = "sql/load.psql",
            Profile = EScriptProfile.ManySmall,
            BudgetBytes = 204_800,
            LargestTable = new TableRef("public", "a"),
            LargestTableBytes = 100_000,
            Rows = 900,
        };

        // Act
        var document = ReportModel.Document(TwoTables(), scripts: scripts);

        // Assert — the sentence says why the files exist at all, not just how many there are.
        Section(document, "## SQL scripts").Should().Contain(
            "The largest table, `public.a` (97.7 KiB), is within the 200.0 KiB per-file budget and the catalog "
            + "does not fit one file, so its tables are packed into 2 files.");
    }

    [Fact]
    public void Render_ForASchemaOnlyCatalog_SaysTheRowsAreWhatIsMissing()
    {
        // Arrange — the catalog was captured without rows, so the pass wrote the schema file alone.
        var scripts = new ScriptResult
        {
            SchemaPath = "sql/schema.sql",
            SchemaBytes = 120,
            SchemaStatements = 7,
            Profile = EScriptProfile.NoRows,
        };

        // Act
        var document = ReportModel.Document(TwoTables(), scripts: scripts);

        // Assert (D2) — the profile is stated rather than left for the reader to infer from an absent file.
        Section(document, "## SQL scripts").Should().Contain(
            "The catalog holds no rows, so no data file was written.");
    }

    [Fact]
    public void Render_WhenTheDataFileInlinesPayloads_SaysWhyItCanBeLarger()
    {
        // Arrange — a catalog whose data file carries the bytes a restore binds as parameters (D5).
        var scripts = new ScriptResult
        {
            SchemaPath = "sql/schema.sql",
            SchemaBytes = 120,
            SchemaStatements = 7,
            DataFiles = [new ScriptDataFile("sql/data.sql", 4096, 9, 20, null)],
            Rows = 20,
            PayloadBytes = 3072,
        };

        // Act
        var document = ReportModel.Document(TwoTables(), scripts: scripts);

        // Assert — the size is stated, not capped, so the report says where it comes from.
        Section(document, "## SQL scripts").Should().Contain(
            "`sql/data.sql` inlines 3072 bytes of binary payloads as base64, which is why it can be larger "
            + "than the data it describes.");
    }

    [Fact]
    public void Render_ForASchemaOnlyScript_SaysTheTailIsInTheSchemaFile()
    {
        // Arrange — a schema-only catalog writes no data file, so the key validation belongs to the one file.
        var scripts = new ScriptResult
        {
            SchemaPath = "sql/schema.sql",
            SchemaBytes = 120,
            SchemaStatements = 7,
        };

        // Act
        var document = ReportModel.Document(TwoTables(), scripts: scripts);

        // Assert (D2) — the sentence names the file that was written rather than assuming two.
        Section(document, "## SQL scripts").Should().Contain(
            "Foreign keys are added unvalidated and validated at the end of `sql/schema.sql`.");
        document.Should().NotContain("`sql/data.sql`");
    }

    [Fact]
    public void Render_ForARefusedScriptPass_StatesTheReasonInOneSentence()
    {
        // Arrange
        var scripts = new ScriptResult
        {
            Refusal = new ScriptRefusal
            {
                Code = "SqlScriptsRefused",
                Reason = "the catalog is written in the \"mysql\" dialect",
            },
        };

        // Act
        var document = ReportModel.Document(TwoTables(), scripts: scripts);

        // Assert (D11) — a reader who asked for scripts and got none deserves the reason where they look.
        Section(document, "## SQL scripts").Should().Equal(
            "the catalog is written in the \"mysql\" dialect");
        document.Should().NotContain("| file | bytes |");
    }

    [Fact]
    public void Render_ForACancelledScriptPass_SaysThePassWasCancelled()
    {
        // Arrange
        var scripts = new ScriptResult { Cancelled = true };

        // Act
        var document = ReportModel.Document(TwoTables(), scripts: scripts);

        // Assert — a cancelled pass is not a refusal, so it states what happened and names no file.
        Section(document, "## SQL scripts").Should().Equal(
            "no script was written — the pass was cancelled");
    }

    private static InterchangeSnapshot TwoTables() =>
        ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "a"),
            ReportModel.KeyedTable("public", "b"));

    private static string[] Lines(string document) => document.Split('\n');

    private static string[] Cells(string row) =>
        [.. Regex.Split(row, @"(?<!\\)\|").Select(cell => cell.Trim())];

    /// <summary>The non-blank lines printed under one level-2 heading, up to the next heading.</summary>
    private static List<string> Section(string document, string heading)
    {
        var lines = Lines(document);
        var start = Array.IndexOf(lines, heading);

        start.Should().BeGreaterThan(-1, $"the document must still print {heading}");

        var section = new List<string>();
        for (var i = start + 1; i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
        {
            if (lines[i].Length > 0)
            {
                section.Add(lines[i]);
            }
        }

        return section;
    }

    private static string Matches(string document, string pattern) =>
        string.Join(", ", Regex.Matches(document, pattern).Select(m => m.Value));
}
