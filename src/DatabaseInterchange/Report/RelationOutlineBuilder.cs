namespace DatabaseInterchange.Report;

/// <summary>
/// Turns one group into the indented text walk of §5.6 — or into the single line that says no walk can
/// be made complete for it.
/// </summary>
/// <remarks>
/// <para>
/// A walk is attempted only where it can be printed whole: a group of more than
/// <see cref="ReportBudget.MaxOutlineTables"/> tables gets the refusal line and nothing else. The
/// vocabulary of this class therefore contains <em>no omission marker of any kind</em> — no depth cap,
/// no sibling fold, no "and N more" — because a walk that would have to fold is not produced at all
/// (FR7b).
/// </para>
/// <para>
/// What is printed is a <em>tree</em> view of the group, so a cycle shows as the <c>↺</c> mark on the
/// node that closes it rather than as a second row, and the caption beside it points the reader at the
/// register for the complete account. Declining a walk is not a cut: every table and constraint it does
/// not show is printed in full in that group's register and in the cards.
/// </para>
/// </remarks>
internal sealed class RelationOutlineBuilder
{
    private readonly Dictionary<string, List<string>> _dependents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
    private readonly List<string> _lineage = [];

    /// <summary>
    /// Builds the outline for one group.
    /// </summary>
    /// <param name="group">The group whose tables the walk must cover.</param>
    /// <param name="inboundCounts">Relations pointing at each table, used to pick the root (§5.6).</param>
    /// <returns>An <see cref="Outline"/> holding either the walk lines or the single refusal line.</returns>
    public Outline Build(RelationGroup group, IReadOnlyDictionary<string, int> inboundCounts)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(inboundCounts);

        if (group.TableCount > ReportBudget.MaxOutlineTables)
        {
            return new Outline(false, [Refusal(group)]);
        }

        if (group.TableCount == 0)
        {
            return new Outline(true, [ReportText.None]);
        }

        var lines = new List<string>();
        PrepareWalk(group);

        // Every table of the group appears exactly once, so a component the root cannot reach by
        // following dependents gets its own top-level line rather than being dropped.
        foreach (var start in RemainingRoots(inboundCounts))
        {
            if (_visited.Contains(start))
            {
                continue;
            }

            Walk(lines, start, string.Empty, true, true);
        }

        return new Outline(true, lines);
    }

    /// <summary>
    /// §5.6's refusal: the report states which shapes refuse to be compressed instead of returning a
    /// tidy picture that quietly omitted a third of the schema.
    /// </summary>
    private static string Refusal(RelationGroup group) =>
        $"Group {group.Number} — {group.TableCount} tables, {group.RelationCount} relations: " +
        "too interconnected to summarise as a walk; use the register and the cards.";

    private void PrepareWalk(RelationGroup group)
    {
        _dependents.Clear();
        _visited.Clear();

        foreach (var member in group.Members)
        {
            _dependents[member] = [];
        }

        // The walk runs from a parent down to the tables that depend on it, which is the direction a
        // reader follows when asking "what breaks if I delete this row".
        foreach (var edge in group.Edges.Where(e => e.ParentIsCaptured && !e.IsSelfReference))
        {
            if (_dependents.TryGetValue(edge.ParentName, out var dependents)
                && !dependents.Contains(edge.ChildName))
            {
                dependents.Add(edge.ChildName);
            }
        }

        foreach (var list in _dependents.Values)
        {
            list.Sort(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The tables the walk must still cover, in order: the group's highest fan-in table first (ties
    /// broken ordinal), then any member a previous root could not reach.
    /// </summary>
    private List<string> RemainingRoots(IReadOnlyDictionary<string, int> inboundCounts) =>
        [.. _dependents.Keys
            .OrderByDescending(name => inboundCounts.TryGetValue(name, out var count) ? count : 0)
            .ThenBy(name => name, StringComparer.Ordinal)];

    private void Walk(
        List<string> lines,
        string node,
        string prefix,
        bool isLastChild,
        bool isRoot)
    {
        _visited.Add(node);
        _lineage.Add(node);

        var children = _dependents.TryGetValue(node, out var found)
            ? found.Where(child => !_visited.Contains(child)).ToList()
            : [];

        lines.Add(FormatLine(node, prefix, isLastChild, isRoot));

        var childPrefix = isRoot ? string.Empty : prefix + (isLastChild ? "    " : "│   ");
        for (var i = 0; i < children.Count; i++)
        {
            Walk(lines, children[i], childPrefix, i == children.Count - 1, false);
        }

        _lineage.RemoveAt(_lineage.Count - 1);
    }

    private string FormatLine(string node, string prefix, bool isLastChild, bool isRoot)
    {
        var branch = isRoot ? string.Empty : isLastChild ? "└── " : "├── ";

        // A back edge is not a second row: it is the ↺ mark on the node whose own foreign key returns
        // to one of its ancestors in this walk (§5.6).
        var closesCycle = ClosesBackEdge(node);

        return $"{prefix}{branch}{ReportText.Cell(node)}{(closesCycle ? " ↺" : string.Empty)}";
    }

    /// <summary>
    /// Whether some table above this node in the walk points at the node itself, which is what closes a
    /// cycle. The tree edge that brought the node here runs the other way — a dependent references its
    /// parent — so counting that relation would mark every line of every acyclic walk (§5.6).
    /// </summary>
    private bool ClosesBackEdge(string node)
    {
        List<string> dependents = _dependents.TryGetValue(node, out var found) ? found : [];

        for (var i = 0; i < _lineage.Count - 1; i++)
        {
            if (dependents.Contains(_lineage[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The result of one group's outline: whether it is a walk the reader may trust to be whole, and the
    /// lines that make it up.
    /// </summary>
    /// <param name="IsWalk">
    /// <see langword="true"/> for a complete walk; <see langword="false"/> when the group was too
    /// interconnected and <paramref name="Lines"/> is the single refusal statement.
    /// </param>
    /// <param name="Lines">The outline lines, without heading or caption.</param>
    internal sealed record Outline(bool IsWalk, IReadOnlyList<string> Lines);
}
