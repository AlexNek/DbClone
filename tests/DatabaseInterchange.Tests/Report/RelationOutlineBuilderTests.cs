using CanonicalModel.Interchange;

using DatabaseInterchange.Report;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Report;

/// <summary>
/// The outline is the one place the report may decline to draw a picture, so the rule under test is that
/// declining is total: a group too large gets the refusal line and nothing else, and a group small enough
/// to walk is walked whole (§5.6, FR7b).
/// </summary>
public sealed class RelationOutlineBuilderTests
{
    private readonly RelationOutlineBuilder _builder = new();

    [Fact]
    public void Build_WalksEveryTableOfASevenTableGroupExactlyOnce()
    {
        // Arrange — one hub and six dependents: seven tables, one group, no cycle.
        var group = GroupOf(ReportModel.Star(6));

        // Act
        var outline = _builder.Build(group, InboundCounts(group));

        // Assert
        outline.IsWalk.Should().BeTrue();
        outline.Lines.Should().HaveCount(group.TableCount);
        outline.Lines.Select(NodeOf).Should().BeEquivalentTo(group.Members);
        outline.Lines.Should().OnlyContain(line => !line.Contains('↺'));
    }

    [Fact]
    public void Build_PrintsOneLinePerTreeEdgeOfTheGroup()
    {
        // Arrange — the same star: six constraints, so six dependent lines under the hub's own.
        var group = GroupOf(ReportModel.Star(6));
        var treeEdges = group.Edges.Where(e => e.ParentIsCaptured && !e.IsSelfReference).ToList();

        // Act
        var outline = _builder.Build(group, InboundCounts(group));

        // Assert — a printed child line is a tree edge, and every dependent of the group is printed.
        List<string> children = [.. outline.Lines.Skip(1)];
        outline.Lines.Should().HaveCount(1 + treeEdges.Count);
        outline.Lines[0].Should().StartWith("public.hub");
        children.Should().OnlyContain(line =>
            line.StartsWith("├── ", StringComparison.Ordinal) || line.StartsWith("└── ", StringComparison.Ordinal));
        children.Select(NodeOf).Should().BeEquivalentTo(
            treeEdges.Select(edge => edge.ChildName).Distinct());
    }

    [Fact]
    public void Build_MarksTheNodeThatClosesABackEdge_InsteadOfRepeatingARow()
    {
        // Arrange — two tables that reference each other (§5.6's tree view of a cycle).
        var snapshot = ReportModel.Snapshot(
            Dependent("public", "a", Reference("public", "a", "fk_a_b", "b", "b_id")),
            Dependent("public", "b", Reference("public", "b", "fk_b_a", "a", "a_id")));
        var group = GroupOf(snapshot);

        // Act
        var outline = _builder.Build(group, InboundCounts(group));

        // Assert — the cycle is one mark on the closing node, not a second visit.
        outline.IsWalk.Should().BeTrue();
        outline.Lines.Should().HaveCount(2);
        outline.Lines.Select(NodeOf).Should().OnlyHaveUniqueItems();
        outline.Lines.Single(line => line.EndsWith(" ↺", StringComparison.Ordinal))
            .Should().Contain("public.b");
        outline.Lines[0].Should().NotEndWith(" ↺", "the root has no ancestor to return to");
    }

    [Fact]
    public void Build_GivesAnUnreachableMemberItsOwnTopLevelLine()
    {
        // Arrange — a chain whose top the walk can only reach by starting there.
        var snapshot = ReportModel.Snapshot(
            Dependent("public", "child", Reference("public", "child", "fk_child_mid", "mid", "mid_id")),
            Dependent("public", "mid", Reference("public", "mid", "fk_mid_top", "top", "top_id")),
            ReportModel.KeyedTable("public", "top"));
        var group = GroupOf(snapshot);

        // Act
        var outline = _builder.Build(group, InboundCounts(group));

        // Assert — nothing is dropped: all three tables appear, and the one the first root could not
        // reach downward is printed as a root of its own.
        outline.IsWalk.Should().BeTrue();
        outline.Lines.Select(NodeOf).Should().BeEquivalentTo(group.Members);
        outline.Lines.Should().Contain(line => line.StartsWith("public.top", StringComparison.Ordinal)
                                               || NodeOf(line) == "public.top");
    }

    [Fact]
    public void Build_YieldsExactlyTheRefusalLineForAGroupTooLargeToWalk()
    {
        // Arrange — the budget's limit plus one table.
        var group = GroupOf(ReportModel.Star(ReportBudget.MaxOutlineTables));
        group.TableCount.Should().Be(ReportBudget.MaxOutlineTables + 1);

        // Act
        var outline = _builder.Build(group, InboundCounts(group));

        // Assert — a refusal, stated once, with no walk beside it.
        outline.IsWalk.Should().BeFalse();
        outline.Lines.Should().ContainSingle();
        outline.Lines[0].Should().Be(
            $"Group {group.Number} — {group.TableCount} tables, {group.RelationCount} relations: " +
            "too interconnected to summarise as a walk; use the register and the cards.");
        outline.Lines[0].Should().NotContain("├── ").And.NotContain("└── ");
    }

    [Fact]
    public void Build_NeverEmitsAnOmissionMarker_ForAnyInput()
    {
        // Arrange — a walk, a refusal, an empty group and a cyclic group.
        var snapshots = new[]
        {
            ReportModel.Star(3),
            ReportModel.Star(ReportBudget.MaxOutlineTables + 2),
            ReportModel.Snapshot(
                Dependent("public", "a", Reference("public", "a", "fk_a_b", "b", "b_id")),
                Dependent("public", "b", Reference("public", "b", "fk_b_a", "a", "a_id"))),
        };

        // Act
        var lines = snapshots
            .Select(ReportModel.MapOf)
            .SelectMany(map => map.Groups)
            .SelectMany(group => _builder.Build(group, InboundCounts(group)).Lines)
            .ToList();

        // Assert (FR7b) — the vocabulary of a cut is absent, because a walk that would need it is not
        // produced at all.
        lines.Should().NotBeEmpty();
        lines.Should().OnlyContain(line => !ContainsOmissionVocabulary(line));
    }

    [Fact]
    public void Build_YieldsTheSameLinesOnEveryRunForTheSameGroup()
    {
        // Arrange — the sibling order of a star is decided by ordinal sort, not by insertion (§D9.2).
        var group = GroupOf(ReportModel.Star(7));

        // Act
        var first = string.Join("\n", _builder.Build(group, InboundCounts(group)).Lines);
        var second = string.Join("\n", new RelationOutlineBuilder()
            .Build(GroupOf(ReportModel.Star(7)), InboundCounts(group)).Lines);

        // Assert
        second.Should().Be(first);
    }

    [Fact]
    public void Build_RejectsAMissingGroupOrCounting()
    {
        var inbound = InboundCounts(GroupOf(ReportModel.Star(2)));

        var build = () => _builder.Build(null!, inbound);
        var count = () => _builder.Build(GroupOf(ReportModel.Star(2)), null!);

        build.Should().Throw<ArgumentNullException>();
        count.Should().Throw<ArgumentNullException>();
    }

    /// <summary>The single group of a snapshot, which every fixture below is built to have exactly one.</summary>
    private static RelationGroup GroupOf(InterchangeSnapshot snapshot)
    {
        var map = ReportModel.MapOf(snapshot);

        return map.Groups.Should().ContainSingle().Subject;
    }

    /// <summary>The relations pointing at each table, which is how §5.6 picks the root.</summary>
    private static Dictionary<string, int> InboundCounts(RelationGroup group) =>
        group.Members.ToDictionary(
            name => name,
            name => group.Edges.Count(edge => edge.ParentIsCaptured && edge.ParentName == name),
            StringComparer.Ordinal);

    private static string NodeOf(string line)
    {
        var text = line;
        var gate = Math.Max(
            text.LastIndexOf("├── ", StringComparison.Ordinal),
            text.LastIndexOf("└── ", StringComparison.Ordinal));
        if (gate >= 0)
        {
            text = text[(gate + 4)..];
        }

        return text.Replace(" ↺", string.Empty, StringComparison.Ordinal).Trim();
    }

    private static bool ContainsOmissionVocabulary(string line) =>
        line.Contains("…", StringComparison.Ordinal)
        || line.Contains("...", StringComparison.Ordinal)
        || line.Contains("more", StringComparison.Ordinal)
        || line.Contains("listed", StringComparison.Ordinal)
        || line.Contains("omitted", StringComparison.Ordinal)
        || line.Contains("truncated", StringComparison.Ordinal)
        || line.Contains("folded", StringComparison.Ordinal)
        || line.Contains("remaining", StringComparison.Ordinal);

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
}
