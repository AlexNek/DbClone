using System.Globalization;
using System.Text.RegularExpressions;

using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DatabaseInterchange.Report;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Report;

/// <summary>
/// FR16 and §5.10: the <c>## Catalog observations</c> index. The section is a restatement, so its whole
/// contract is that it says nothing the detail does not, in a number the detail does not print, and in a
/// tone the report never uses — an index that drifts from the sections it points at is worse than none,
/// because a reader trusts the short list first.
/// </summary>
public sealed class CatalogReportObservationsTests
{
    /// <summary>Each finding the index states in exactly one line, whatever its count (§5.10).</summary>
    private static readonly string[] SingleLineFindings =
    [
        "tables have no primary key",
        "cyclic groups",
        "self references",
        "foreign keys reference tables outside this catalog",
        "tables have no foreign-key relationships",
        "table pairs are joined by more than one constraint",
        "tables are stored in source order",
    ];

    /// <summary>The fan-in lines are the finding allowed more than one entry, bounded by the ranking view.</summary>
    private const string FanInMarker = "is referenced by";

    private static readonly Regex LeadingNumber = new(@"(\d+)", RegexOptions.CultureInvariant);

    /// <summary>One numbered group in <c>### Cyclic groups</c>: its size in tables.</summary>
    private static readonly Regex CyclicGroupLine = new(@"^(\d+)\. (\d+) tables — ", RegexOptions.CultureInvariant);

    /// <summary>The cycle observation: group count, then the tables involved in total.</summary>
    private static readonly Regex CyclicObservation =
        new(@"(\d+) cyclic groups, (\d+) tables involved in total", RegexOptions.CultureInvariant);

    /// <summary>A fan-in observation: the table the line names and the count it states.</summary>
    private static readonly Regex FanInObservation =
        new(@"^- `(.+?)` is referenced by (\d+) foreign keys", RegexOptions.CultureInvariant);

    /// <summary>The section reference an observation ends with, so it can be checked against the document.</summary>
    private static readonly Regex Pointer = new("(#{2,3} [A-Za-z][^—]*)$", RegexOptions.CultureInvariant);

    [Fact]
    public void Observations_PrintOneIndexedLine_ForEveryFindingTheCatalogHas()
    {
        // Act
        var lines = ObservationLines(RichDocument());

        // Assert (§5.10) — each finding is indexed once; only the fan-in view may repeat, because it names
        // tables rather than restating a count.
        lines.Should().OnlyContain(line => line.StartsWith("- ", StringComparison.Ordinal));

        foreach (var marker in SingleLineFindings)
        {
            lines.Should().Contain(l => l.Contains(marker, StringComparison.Ordinal), $"one line for {marker}");
            lines.Count(l => l.Contains(marker, StringComparison.Ordinal))
                .Should().Be(1, $"{marker} is stated once, not once per table");
        }

        lines.Count(l => l.Contains(FanInMarker, StringComparison.Ordinal))
            .Should().BeGreaterThan(0, "the catalog has referenced tables");
    }

    [Fact]
    public void Observations_SayNothing_AboutTheFindingsThisSchemaDoesNotHave()
    {
        // Arrange — two keyed tables joined by one foreign key: the only unusual thing is the widest fan-in.
        var lines = ObservationLines(ReportModel.Document(TwoLinkedTables()));

        // Assert — a zero is not a finding, so no line is printed for it: nothing here reads "0 tables have
        // no primary key", which would tell the reader the report checked something it did not.
        lines.Should().ContainSingle(l => l.Contains(FanInMarker, StringComparison.Ordinal));

        foreach (var marker in SingleLineFindings)
        {
            lines.Should().NotContain(l => l.Contains(marker, StringComparison.Ordinal), $"no line for {marker}");
        }
    }

    [Fact]
    public void Observations_PrintNoFanInLine_WhenNoTableIsReferenced()
    {
        // Arrange — one keyed table with no foreign key in either direction.
        var lines = ObservationLines(
            ReportModel.Document(ReportModel.Snapshot(ReportModel.KeyedTable("public", "settings"))));

        // Assert — the ranking is empty, so the index names no gravity well; the table's own listing does.
        lines.Should().ContainSingle();
        lines[0].Should().Contain("tables have no foreign-key relationships");
        lines[0].Should().NotContain(FanInMarker);
    }

    [Fact]
    public void Observations_DegradeToOneStatedLine_ForACatalogWithNothingUnusual()
    {
        // Act
        var lines = ObservationLines(ReportModel.Document(ReportModel.Snapshot()));

        // Assert (AC7) — an empty catalog still gets a section, and it states that it found nothing.
        lines.Should().Equal("nothing unusual in this catalog");
    }

    [Fact]
    public void Observations_MissingPrimaryKeyLine_CountsTheRowsTheStatisticsTableMarks()
    {
        // Arrange
        var document = RichDocument();
        var (header, rows) = TableAfter(SectionLines(document, "## Statistics per table"), "| ");

        // Act
        var withoutKey = rows.Count(row => row[Column(header, "PK")] == "no");

        // Assert (§5.10) — the index's count and its total are the statistics table's own rows, re-counted.
        ObservationCount(ObservationLines(document), "tables have no primary key").Should().Be(withoutKey);
        ObservationLines(document).Should().ContainSingle(
            l => l.Contains($"(of {rows.Count})", StringComparison.Ordinal),
            "the denominator is stated beside the count, so a reader can tell a share from a total");
    }

    [Fact]
    public void Observations_CyclicLine_CountsTheGroupsAndTablesTheDetailLists()
    {
        // Arrange
        var document = RichDocument();
        var groups = SectionLines(document, "### Cyclic groups")
            .Select(line => CyclicGroupLine.Match(line))
            .Where(match => match.Success)
            .ToList();

        // Act
        var stated = CyclicObservation.Match(
            ObservationLines(document).Single(l => l.Contains("cyclic groups", StringComparison.Ordinal)));

        // Assert — both numbers are the list below them, read back rather than recomputed.
        stated.Success.Should().BeTrue("the cycle observation states a group count and a table count");
        int.Parse(stated.Groups[1].Value, CultureInfo.InvariantCulture).Should().Be(groups.Count);
        int.Parse(stated.Groups[2].Value, CultureInfo.InvariantCulture)
            .Should().Be(groups.Sum(match => int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Observations_SelfReferenceAndExternalLines_CountTheBulletsTheyPointAt()
    {
        // Arrange
        var document = RichDocument();
        var lines = ObservationLines(document);

        // Assert (§5.10) — the two lists are the count, item by item.
        ObservationCount(lines, "self references")
            .Should().Be(Bullets(SectionLines(document, "### Self references")).Count);
        ObservationCount(lines, "foreign keys reference tables outside this catalog")
            .Should().Be(Bullets(SectionLines(document, "### References outside this catalog")).Count);
    }

    [Fact]
    public void Observations_NoRelationshipLine_CountsTheRowsOfTheTableItPointsAt()
    {
        // Arrange
        var document = RichDocument();
        var (_, rows) = TableAfter(SectionLines(document, "### Tables with no foreign-key relationships"), "| ");

        // Act
        var stated = ObservationCount(ObservationLines(document), "tables have no foreign-key relationships");

        // Assert — the listing names every one of them, so the count cannot outrun or lag the section.
        stated.Should().Be(rows.Count);
        rows.Select(row => row[0]).Should().Contain("crm.audit", "a table with no relation is listed by name");
    }

    [Fact]
    public void Observations_DuplicatePairLine_CountsThePairsCouplingNames()
    {
        // Arrange
        var document = RichDocument();
        var pairs = BulletBlock(
            SectionLines(document, "### Coupling"),
            "Pairs joined by more than one constraint:");

        // Assert
        ObservationCount(ObservationLines(document), "table pairs are joined by more than one constraint")
            .Should().Be(pairs.Count);
    }

    [Fact]
    public void Observations_SourceOrderLine_CountsTheTablesDataTotalsNames()
    {
        // Arrange
        var document = RichDocument();

        // Assert — the index and the prose under <c>## Data totals</c> name the same tables, so a reader who
        // goes to the section finds every one the line promised (FR4).
        ObservationCount(ObservationLines(document), "tables are stored in source order")
            .Should().Be(SourceOrderNames(document).Count);
        SourceOrderNames(document).Should().Equal("`crm.audit`", "`public.log`");
    }

    [Fact]
    public void Observations_FanInLines_StateTheCountsTheCouplingRankingPrints()
    {
        // Arrange
        var document = RichDocument();
        var (header, rows) = TableAfter(SectionLines(document, "### Coupling"), "Fan-in — the gravity wells");
        var referencedBy = Column(header, "referenced by");

        // Act
        var lines = ObservationLines(document).Where(l => l.Contains(FanInMarker, StringComparison.Ordinal)).ToList();

        // Assert (§5.10) — every named table's number is the ranking's own cell for that table.
        lines.Should().NotBeEmpty();
        lines.Should().HaveCountLessOrEqualTo(ReportBudget.TopFanInInObservations);

        foreach (var line in lines)
        {
            var stated = FanInObservation.Match(line);
            stated.Success.Should().BeTrue($"{line} names a table and its fan-in count");
            var table = rows.Single(row => row[0] == stated.Groups[1].Value);
            int.Parse(table[referencedBy], CultureInfo.InvariantCulture)
                .Should().Be(int.Parse(stated.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        lines[0].Should().Contain("the widest fan-in in this catalog");
        lines.Skip(1).Should().NotContain(l => l.Contains("widest fan-in", StringComparison.Ordinal));
    }

    [Fact]
    public void Observations_EveryPointerNamesASectionTheDocumentActuallyHas()
    {
        // Act
        var document = RichDocument();
        var headings = document.Split('\n').Where(line => line.StartsWith('#')).ToHashSet(StringComparer.Ordinal);

        // Assert — the section is an index, so its value is the target: a pointer to a renamed heading would
        // send the reader to a section that is not there.
        foreach (var line in ObservationLines(document))
        {
            var pointer = Pointer.Match(line);
            pointer.Success.Should().BeTrue($"{line} must point at the section that holds its items");
            headings.Should().Contain(pointer.Groups[1].Value.TrimEnd(), $"the document must print {pointer.Groups[1].Value}");
        }
    }

    [Fact]
    public void Observations_CyclicLine_AndTheSelfReferenceLine_CountDisjointSets()
    {
        // Arrange — three two-table cycles and four self loops. A cycle is a component of two or more
        // tables (§5.5); a loop back into one's own table is not (§5.8), so the two lines measure
        // different edges and neither may absorb the other's count.
        var snapshot = RichSnapshot();
        var document = ReportModel.Document(snapshot, RichStatistics(snapshot));
        var lines = ObservationLines(document);
        var map = ReportModel.MapOf(snapshot);

        // Assert — each line states its own number, and neither states the sum a careless reader would make.
        ObservationCount(lines, "cyclic groups").Should().Be(3);
        ObservationCount(lines, "self references").Should().Be(4);
        lines.Single(l => l.Contains("cyclic groups", StringComparison.Ordinal)).Should().NotContain("7");
        lines.Single(l => l.Contains("self references", StringComparison.Ordinal)).Should().NotContain("7");

        // Assert — no constraint is counted in both halves, so no edge is reported twice.
        map.TablesInCycles.Should().Be(6);
        map.CyclicGroups.SelectMany(group => group.Constraints)
            .Should().NotIntersectWith(map.SelfReferences.Select(edge => edge.Constraint));

        // Assert — and no self-referencing table is a member of a cyclic group in the detail either.
        var members = CyclicMembers(document);
        SelfReferencedTables(document).Should().NotContain(members);
    }

    [Fact]
    public void Observations_NeverGrowPastTheBudget_AndNeverLoseAFindingToReachIt()
    {
        // Act
        var lines = ObservationLines(RichDocument());
        var fanInLines = lines.Count(l => l.Contains(FanInMarker, StringComparison.Ordinal));

        // Assert (§5.10) — the bound is a guard on a derived view, not a cut the product performs: seven
        // findings print one line each and the fan-in names are capped, so a catalog cannot reach the
        // ceiling by having more findings. The one bounded part is a ranking every row of which
        // <c>### Coupling</c> prints.
        lines.Count.Should().Be(SingleLineFindings.Length + fanInLines);
        fanInLines.Should().BeLessThanOrEqualTo(ReportBudget.TopFanInInObservations);
        lines.Count.Should().BeLessThanOrEqualTo(ReportBudget.MaxObservations);
    }

    [Theory]
    [InlineData("should")]
    [InlineData("consider")]
    [InlineData("recommend")]
    [InlineData("suggest")]
    public void Observations_StateTheFact_WithoutAdvisingTheReader(string word)
    {
        // Arrange — every shape of catalog the section can be asked to index.
        var documents = new[]
        {
            RichDocument(),
            ReportModel.Document(TwoLinkedTables()),
            ReportModel.Document(ReportModel.Snapshot(ReportModel.KeyedTable("public", "settings"))),
            ReportModel.Document(ReportModel.Snapshot()),
        };

        // Assert (§5.10) — the report states what the catalog holds; advice is the reader's decision.
        foreach (var document in documents)
        {
            foreach (var line in ObservationLines(document))
            {
                Regex.IsMatch(line, $@"\b{word}\w*", RegexOptions.IgnoreCase)
                    .Should().BeFalse($"'{word}' would be advice, not a fact: {line}");
            }
        }
    }

    /// <summary>
    /// A catalog holding every finding at once: three cyclic groups, four self references, two references
    /// out of the catalog, five duplicate pairs, three tables in no relationship and three without a
    /// primary key, plus two tables written in source order.
    /// </summary>
    private static InterchangeSnapshot RichSnapshot() =>
        ReportModel.SnapshotFrom([
            .. MutualPair("public", "a", "b"),
            .. MutualPair("public", "c", "d"),
            .. MutualPair("crm", "m", "n"),
            SelfLoop("public", "staff", "manager_id"),
            SelfLoop("public", "tree", "parent_id"),
            SelfLoop("crm", "users", "referrer_id"),
            SelfLoop("crm", "nodes", "prev_id"),
            Keyed("public", "orders", Reference("public", "orders", "warehouse", "stock", "stock_id")),
            Keyed("crm", "tickets", Reference("crm", "tickets", "warehouse", "suppliers", "supplier_id")),
            Keyed("public", "header"),
            Unkeyed(
                "public",
                "line",
                Reference("public", "line", "public", "header", "header_id"),
                Reference("public", "line", "public", "header", "second_header_id")),
            Keyed("crm", "thread"),
            Unkeyed(
                "crm",
                "message",
                Reference("crm", "message", "crm", "thread", "thread_id"),
                Reference("crm", "message", "crm", "thread", "reply_to_id"),
                Reference("crm", "message", "crm", "thread", "quote_of_id")),
            Keyed("public", "settings"),
            Unkeyed("crm", "audit"),
            Unkeyed("public", "log"),
        ]);

    /// <summary>Two keyed tables with one foreign key between them and nothing else to report.</summary>
    private static InterchangeSnapshot TwoLinkedTables() =>
        ReportModel.Snapshot(
            Keyed("public", "customers"),
            Keyed("public", "orders", Reference("public", "orders", "public", "customers", "customer_id")));

    private static IReadOnlyList<TableRef> SourceOrderTables() =>
        [new TableRef("crm", "audit"), new TableRef("public", "log")];

    private static CatalogStatistics RichStatistics(InterchangeSnapshot snapshot) =>
        ReportModel.Statistics(snapshot, sourceOrderTables: SourceOrderTables());

    private static string RichDocument()
    {
        var snapshot = RichSnapshot();

        return ReportModel.Document(snapshot, RichStatistics(snapshot));
    }

    private static TableDefinition Keyed(
        string schema, string name, params ForeignKeyDefinition[] references) =>
        ReportModel.KeyedTable(schema, name) with { ForeignKeys = [.. references] };

    private static TableDefinition Unkeyed(
        string schema, string name, params ForeignKeyDefinition[] references) =>
        ReportModel.Table(schema, name,
            ReportModel.Column(schema, name, "id", nullable: false),
            ReportModel.Column(schema, name, "note")) with
        {
            ForeignKeys = [.. references],
        };

    /// <summary>Two tables whose only relations are two constraints pointing at each other (§5.5).</summary>
    private static TableDefinition[] MutualPair(string schema, string first, string second) =>
    [
        Keyed(schema, first, Reference(schema, first, schema, second, $"{second}_id")),
        Keyed(schema, second, Reference(schema, second, schema, first, $"{first}_id")),
    ];

    /// <summary>One table whose foreign key points back at itself (§5.8) — a loop, never a cycle.</summary>
    private static TableDefinition SelfLoop(string schema, string name, string column) =>
        Keyed(schema, name, Reference(schema, name, schema, name, column));

    private static ForeignKeyDefinition Reference(
        string childSchema, string childTable, string parentSchema, string parentTable, string column) =>
        ReportModel.Reference(
            childSchema, childTable,
            $"fk_{childTable}_{parentTable}_{column}",
            parentSchema, parentTable, [column], ["id"]);

    /// <summary>The lines of one heading's body, up to the next heading of any level.</summary>
    private static IReadOnlyList<string> SectionLines(string document, string heading)
    {
        var lines = document.Split('\n');
        var start = Array.FindIndex(lines, line => line == heading);
        start.Should().NotBe(-1, $"the document must render {heading}");

        var body = new List<string>();
        for (var i = start + 1; i < lines.Length && !lines[i].StartsWith('#'); i++)
        {
            body.Add(lines[i]);
        }

        return body;
    }

    /// <summary>The index section's lines, without the pointer sentence that closes it.</summary>
    private static IReadOnlyList<string> ObservationLines(string document) =>
        [.. SectionLines(document, "## Catalog observations")
            .Where(line => line.Length > 0 && !line.StartsWith("See ", StringComparison.Ordinal))];

    /// <summary>The header and the data rows of the first table at or after <paramref name="marker"/>.</summary>
    private static (string[] Header, IReadOnlyList<string[]> Rows) TableAfter(
        IReadOnlyList<string> body, string marker)
    {
        var after = IndexWhere(body, line => line.StartsWith(marker, StringComparison.Ordinal));
        var headerIndex = IndexWhere(body, after, line => line.StartsWith("| ", StringComparison.Ordinal));
        var header = Cells(body[headerIndex]);
        var rows = new List<string[]>();

        for (var i = headerIndex + 1; i < body.Count; i++)
        {
            if (!body[i].StartsWith("| ", StringComparison.Ordinal))
            {
                break;
            }

            var cells = Cells(body[i]);
            if (!cells.All(cell => cell.Length > 0 && cell.All(character => character == '-')))
            {
                rows.Add(cells);
            }
        }

        return (header, rows);
    }

    private static IReadOnlyList<string> Bullets(IReadOnlyList<string> body) =>
        [.. body.Where(line => line.StartsWith("- ", StringComparison.Ordinal))];

    /// <summary>The bullets that follow <paramref name="marker"/> until its block ends.</summary>
    private static IReadOnlyList<string> BulletBlock(IReadOnlyList<string> body, string marker)
    {
        var start = IndexWhere(body, line => line.StartsWith(marker, StringComparison.Ordinal));
        var bullets = new List<string>();

        for (var i = start + 1; i < body.Count; i++)
        {
            if (!body[i].StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            bullets.Add(body[i]);
        }

        return bullets;
    }

    private static string[] Cells(string row) =>
        [.. row.Trim('|').Split(" | ", StringSplitOptions.None).Select(cell => cell.Trim())];

    private static int IndexWhere(IReadOnlyList<string> lines, Func<string, bool> predicate) =>
        IndexWhere(lines, 0, predicate);

    private static int IndexWhere(IReadOnlyList<string> lines, int from, Func<string, bool> predicate)
    {
        var index = lines.Skip(from).ToList().FindIndex(l => predicate(l));
        index.Should().NotBe(-1, "the observation section points at content the document has to print");

        return index + from;
    }

    private static int Column(string[] header, string title)
    {
        var index = Array.IndexOf(header, title);
        index.Should().NotBe(-1, $"the table must carry a {title} column");

        return index;
    }

    /// <summary>The tables <c>### Cyclic groups</c> names as its members.</summary>
    private static IReadOnlyList<string> CyclicMembers(string document) =>
        [.. SectionLines(document, "### Cyclic groups")
            .Where(line => CyclicGroupLine.IsMatch(line))
            .SelectMany(line => BacktickedNames(line))];

    /// <summary>The dependent tables <c>### Self references</c> lists, taken from its bullets.</summary>
    private static IReadOnlyList<string> SelfReferencedTables(string document) =>
        [.. Bullets(SectionLines(document, "### Self references"))
            .Select(line => Backticks.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)];

    private static IReadOnlyList<string> BacktickedNames(string line) =>
        [.. Backticks.Matches(line).Select(match => match.Groups[1].Value).Where(name => name.Contains('.'))];

    private static readonly Regex Backticks = new(@"`([^`]+)`", RegexOptions.CultureInvariant);

    /// <summary>The tables the <c>## Data totals</c> prose names as stored in source order.</summary>
    private static IReadOnlyList<string> SourceOrderNames(string document)
    {
        const string marker = "not guaranteed to repeat: ";
        var line = SectionLines(document, "## Data totals")
            .Single(l => l.Contains("tables stored in source order", StringComparison.Ordinal));
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"{line} must name the tables it counts");

        return [.. line[(start + marker.Length)..].Split(", ", StringSplitOptions.None)];
    }

    /// <summary>The count a stated observation opens with.</summary>
    private static int ObservationCount(IReadOnlyList<string> observations, string marker)
    {
        var lines = observations.Where(l => l.Contains(marker, StringComparison.Ordinal)).ToList();
        lines.Should().ContainSingle($"exactly one observation states the {marker} fact");

        return FirstNumber(lines[0]);
    }

    private static int FirstNumber(string line)
    {
        var match = LeadingNumber.Match(line);
        match.Success.Should().BeTrue($"{line} must state a count");

        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
