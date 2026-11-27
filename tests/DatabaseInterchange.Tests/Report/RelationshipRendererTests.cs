using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

using CanonicalModel.Interchange;

using DatabaseInterchange.Report;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Report;

/// <summary>
/// The relationship half of the document. Its central promise is FR7b: the printed count and the printed
/// lines are the same number, at any size, and no wording anywhere admits that something was left out.
/// </summary>
public sealed class RelationshipRendererTests
{
    private readonly RelationshipRenderer _renderer = new();

    /// <summary>The register's first cell column, so a row can be found by its constraint name.</summary>
    private const string RegisterHeader = "| child | columns |";

    /// <summary>Rendered documents are cached because the 5000-constraint case is re-read by several rules.</summary>
    private static readonly ConcurrentDictionary<int, string> StarDocuments = new();

    [Theory]
    [InlineData(5)]
    [InlineData(1500)]
    [InlineData(5000)]
    public void Render_PrintsOneRegisterRowPerCapturedConstraint(int constraints)
    {
        // Act
        var document = StarDocument(constraints);

        // Assert (FR7b) — rows equal constraints at every size, not a share of them.
        RegisterRowCount(document).Should().Be(constraints);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1500)]
    [InlineData(5000)]
    public void Render_PrintsEveryLineEachCardCounts(int constraints)
    {
        // Act
        var document = StarDocument(constraints);

        // Assert (§5.3) — a card's heading states a number and the block below it holds exactly that many
        // lines; the totals hold too, so no constraint is printed once and counted twice.
        var blocks = CardBlocks(document);
        blocks.Should().NotBeEmpty();
        blocks.Should().OnlyContain(block => block.Printed == block.Stated);
        blocks.Where(block => block.IsOutgoing).Sum(block => block.Printed).Should().Be(constraints);
        blocks.Where(block => !block.IsOutgoing).Sum(block => block.Printed).Should().Be(constraints);
    }

    [Fact]
    public void Render_PrintsAllThreeHundredLinesOfAHeavilyReferencedHub()
    {
        // Act
        var document = StarDocument(300);

        // Assert (§5.3) — a table referenced by 300 others gets 300 lines, which is the case a bounded
        // view would have turned into a summary.
        var hub = CardBlocks(document)
            .Single(block => !block.IsOutgoing && block.Card.Contains("public.hub", StringComparison.Ordinal));
        hub.Stated.Should().Be(300);
        hub.Printed.Should().Be(300);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(5000)]
    public void Render_NeverSpeaksOfAnOmission(int constraints)
    {
        // Act
        var document = StarDocument(constraints);

        // Assert (FR7b) — the wording of a cut is absent, so a future bound cannot return as phrasing.
        document.Should().NotContain("listed below");
        document.Should().NotContain("not listed");
        document.Should().NotContain("the rest are in");
        document.Should().NotContain("folded");
        document.Should().NotContain("schema/tables.json");
        document.Should().NotContain("schema/relationships.json");
        Matches(document, @"\band\b \d+ \bmore\b").Should().BeEmpty();
        Matches(document, @"\bof \d+ listed\b").Should().BeEmpty();
    }

    [Fact]
    public void Render_NeverSpeaksOfAnOmission_ForASchemaWithCyclesSelfAndExternalReferences()
    {
        // Act
        var document = _renderer.Render(
            ReportModel.MapOf(RichSnapshot()), ReportModel.Statistics(RichSnapshot()));

        // Assert — the sections that a bounded renderer would trim are the ones this fixture exercises.
        document.Should().Contain("### Self references");
        document.Should().Contain("### References outside this catalog");
        document.Should().Contain("### Cyclic groups");
        document.Should().Contain("one path among the cycles here");
        Matches(document, @"\band\b \d+ \bmore\b").Should().BeEmpty();
        Matches(document, @"\bnot listed\b").Should().BeEmpty();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(5000)]
    public void Render_EmitsNoDiagramMarkupAndNoFencedBlock(int constraints)
    {
        // Act
        var document = StarDocument(constraints);

        // Assert (§5.6, AC4) — the same bytes are text in every reader, so no picture syntax appears.
        document.Should().NotContain("erDiagram");
        document.Should().NotContain("graph ");
        document.Should().NotContain("digraph");
        document.Should().NotContain("![");
        document.Should().NotContain("```");
    }

    [Fact]
    public void Render_StatesNoneForEveryListWhoseInputIsEmpty()
    {
        // Arrange — two unrelated tables: no constraint, no group, no cycle, no view, no lineage.
        var document = Render(ReportModel.WideSchema(2));

        // Assert (FR10) — an empty list is stated as empty, never printed as a table of nothing.
        LineAfter(document, "### Cyclic groups").Should().Be(ReportText.None);
        LineAfter(document, "### Self references").Should().Be(ReportText.None);
        LineAfter(document, "### References outside this catalog").Should().Be(ReportText.None);
        LineAfter(document, "### Lineage").Should().Be(ReportText.None);
        LineAfter(document, "### Table cards — by relation count").Should().Be(ReportText.None);
        document.Should().Contain("Fan-in — the gravity wells: none");
        document.Should().Contain("Fan-out — the glue tables: none");
        document.Should().Contain("Pairs joined by more than one constraint: none");
        document.Should().Contain("Cross-schema relations: none");
    }

    [Fact]
    public void Render_EscapesANameCarryingTableCellCharacters_InEveryCellItLandsIn()
    {
        // Arrange — one table, one column and one constraint whose names carry a pipe, a backtick, a quote.
        var document = Render(OddNamedSnapshot());

        // Assert (§5.9) — no printed row is split by the characters it names.
        AssertRowsMatchTheirHeaders(document);
        document.Should().Contain(@"public.pipe\|tick");
        Rows(document).Should().OnlyContain(row => !row.Contains("pipe|tick", StringComparison.Ordinal));

        // Outside a table the name is still the name: a heading carries it raw inside its code span.
        var heading = Lines(document).Single(line => line.StartsWith("#### ", StringComparison.Ordinal)
                                                     && line.Contains("pipe|tick", StringComparison.Ordinal));
        heading.Should().Contain("``public.pipe|tick`quote``");
    }

    [Fact]
    public void Render_PrintsADashForAnActionLeftAtItsDefaultAndTheWordsOtherwise()
    {
        // Arrange (§5.2): one explicit CASCADE, one explicit NO ACTION, one with nothing captured.
        var document = Render(ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "customers"),
            Dependent("public", "orders",
                Reference("public", "orders", "fk_cascade", "customers", "a_id", onDelete: "CASCADE"),
                Reference("public", "orders", "fk_no_action", "customers", "b_id", onDelete: "NO ACTION"),
                Reference("public", "orders", "fk_unstated", "customers", "c_id"))));

        // Assert — the register's delete and update cells, and the card line beside them.
        RegisterRow(document, "fk_cascade")[5].Should().Be("cascade");
        RegisterRow(document, "fk_cascade")[6].Should().Be(ReportText.NotStated);
        RegisterRow(document, "fk_no_action")[5].Should().Be(ReportText.NotStated);
        RegisterRow(document, "fk_unstated")[5].Should().Be(ReportText.NotStated);

        // The same rule in both cards the constraint is named in (§5.3's two views of one fact).
        var cardLines = Lines(document)
            .Where(line => line.StartsWith("    ", StringComparison.Ordinal)
                           && line.Contains("fk_no_action", StringComparison.Ordinal))
            .ToList();
        cardLines.Should().HaveCount(2);
        cardLines.Should().OnlyContain(line => !line.Contains("no action", StringComparison.Ordinal));
    }

    private string Render(InterchangeSnapshot snapshot) =>
        _renderer.Render(ReportModel.MapOf(snapshot), ReportModel.Statistics(snapshot));

    private static string StarDocument(int constraints) =>
        StarDocuments.GetOrAdd(constraints, size =>
        {
            var snapshot = ReportModel.Star(size);

            return new RelationshipRenderer().Render(
                ReportModel.MapOf(snapshot), ReportModel.Statistics(snapshot));
        });

    private static string[] Lines(string document) => document.Split('\n');

    private static IEnumerable<string> Rows(string document) =>
        Lines(document).Where(line => line.StartsWith("| ", StringComparison.Ordinal));

    /// <summary>The cells of one printed row, in order, with the row's outer separators removed.</summary>
    private static string[] Cells(string row) =>
        [.. Regex.Split(row, @"(?<!\\)\|").Select(cell => cell.Trim())];

    private static string LineAfter(string document, string heading)
    {
        var lines = Lines(document);
        var index = Array.IndexOf(lines, heading);

        index.Should().BeGreaterThan(-1, $"the document must still print {heading}");

        return lines[index + 1];
    }

    private static int RegisterRowCount(string document)
    {
        var lines = Lines(document);
        var rows = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(RegisterHeader, StringComparison.Ordinal))
            {
                continue;
            }

            for (var j = i + 2; j < lines.Length && lines[j].StartsWith("| ", StringComparison.Ordinal); j++)
            {
                rows++;
            }
        }

        return rows;
    }

    private static List<CardBlock> CardBlocks(string document)
    {
        var lines = Lines(document);
        var blocks = new List<CardBlock>();
        var card = string.Empty;

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("#### ", StringComparison.Ordinal))
            {
                card = lines[i];
                continue;
            }

            var isOutgoing = lines[i].StartsWith("→ References (", StringComparison.Ordinal);
            if (!isOutgoing && !lines[i].StartsWith("← Referenced by (", StringComparison.Ordinal))
            {
                continue;
            }

            var open = lines[i].IndexOf('(');
            var stated = int.Parse(lines[i][(open + 1)..^1], CultureInfo.InvariantCulture);
            var printed = 0;
            for (var j = i + 1; j < lines.Length && lines[j].StartsWith("    ", StringComparison.Ordinal); j++)
            {
                printed++;
            }

            blocks.Add(new CardBlock(card, isOutgoing, stated, printed));
        }

        return blocks;
    }

    /// <summary>The register row printed for one constraint, split into its eight cells.</summary>
    private static IReadOnlyList<string> RegisterRow(string document, string constraint)
    {
        var row = Rows(document).Single(line => Cells(line).Contains(ReportText.Cell(constraint)));

        return [.. Cells(row).Skip(1).Take(8)];
    }

    private static void AssertRowsMatchTheirHeaders(string document)
    {
        var lines = Lines(document);

        for (var i = 0; i + 2 < lines.Length; i++)
        {
            if (!lines[i].StartsWith("| ", StringComparison.Ordinal)
                || !lines[i + 1].StartsWith("| ---", StringComparison.Ordinal))
            {
                continue;
            }

            var headers = Cells(lines[i]).Length;
            for (var j = i + 2; j < lines.Length && lines[j].StartsWith("| ", StringComparison.Ordinal); j++)
            {
                Cells(lines[j]).Length.Should().Be(headers, $"row {lines[j]} breaks the {headers}-column table");
            }
        }
    }

    private static string Matches(string document, string pattern) =>
        string.Join(", ", Regex.Matches(document, pattern).Select(m => m.Value));

    private const string OddName = "pipe|tick`quote";

    private static InterchangeSnapshot OddNamedSnapshot()
    {
        var odd = ReportModel.Table("public", OddName,
            ReportModel.Column("public", OddName, "id", nullable: false),
            ReportModel.Column("public", OddName, OddName, nullable: false)) with
        {
            PrimaryKey = ReportModel.PrimaryKey("public", OddName, "id"),
            ForeignKeys =
            [
                ReportModel.Reference(
                    "public", OddName, $"fk_{OddName}", "public", "customers",
                    [OddName], [OddName], onDelete: "CASCADE"),
            ],
        };

        return ReportModel.Snapshot(ReportModel.KeyedTable("public", "customers"), odd);
    }

    /// <summary>A schema that touches every section of the relationship half at once.</summary>
    private static InterchangeSnapshot RichSnapshot() =>
        ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "customers"),
            Dependent("public", "orders",
                Reference("public", "orders", "fk_orders_customer", "customers", "customer_id")),
            Dependent("public", "employees",
                Reference("public", "employees", "fk_manager", "employees", "manager_id"),
                Reference("public", "employees", "fk_emp_dept", "departments", "dept_id")),
            Dependent("public", "departments",
                Reference("public", "departments", "fk_dept_head", "employees", "head_id")),
            Dependent("public", "archive_rows",
                ReportModel.Reference(
                    "public", "archive_rows", "fk_archive_ghost", "archive", "gone",
                    ["ghost_id"], ["id"]))) with
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
            ],
        };

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
        string childSchema, string childTable, string name, string parentTable, string column,
        string? onDelete = null) =>
        ReportModel.Reference(
            childSchema, childTable, name, childSchema, parentTable, [column], ["id"], onDelete);

    /// <summary>One card block: the heading it sits under, the count it states and the lines it prints.</summary>
    private readonly record struct CardBlock(string Card, bool IsOutgoing, int Stated, int Printed);
}
