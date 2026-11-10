using CanonicalModel.Interchange;

namespace DatabaseInterchange.Report;

/// <summary>
/// Turns the captured tables and their foreign keys into the relation facts the report prints:
/// every constraint as one edge, the groups that hold the register, the per-table neighbourhoods, the
/// coupling rankings and the cyclic components (§5).
/// </summary>
/// <remarks>
/// <para>
/// Pure: the catalog is the only source of truth, so no second query and no connection is made (FR6).
/// Nothing here computes a position, a size or a coordinate — there is nothing to lay out.
/// </para>
/// <para>
/// Every ordering rule is D9.3's: groups by their lowest <c>(schema, table)</c> member, register rows by
/// child → parent → constraint, cards by relation count then name, rankings by count then name, cyclic
/// groups by their sorted member list. Case-sensitive ordinal comparison throughout, matching
/// <see cref="ObjectRef"/>'s own rule, so <c>Mixed</c> and <c>mixed</c> never trade places between runs.
/// </para>
/// </remarks>
internal sealed class RelationGraphBuilder
{
    /// <summary>Gets the relationship type that carries view lineage, kept out of the register (§5.1).</summary>
    internal const string ViewReferenceType = "viewReference";

    /// <summary>
    /// Builds the relation map for one captured snapshot.
    /// </summary>
    public RelationMap Build(InterchangeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var schemaNames = ForeignKeyEdge.SchemaNamesOf(snapshot);
        var captured = CapturedTables(snapshot, schemaNames);
        var edges = BuildEdges(snapshot, schemaNames, captured);
        var cyclicGroups = FindCyclicGroups(edges);
        var cards = BuildCards(edges, cyclicGroups);

        return new RelationMap
        {
            Edges = edges,
            Groups = BuildGroups(edges),
            Cards = cards,
            Coupling = BuildCoupling(edges, cards, captured),
            CyclicGroups = cyclicGroups,
            SelfReferences = [.. edges.Where(e => e.IsSelfReference)],
            ExternalReferences = [.. edges.Where(e => !e.ParentIsCaptured)],
            TablesWithNoRelationships = TablesWithNoRelations(cards, captured),
            Lineage = BuildLineage(snapshot),
        };
    }

    /// <summary>
    /// Enumerates the captured tables as the <c>(schema, table)</c> keys every other rule is keyed by.
    /// </summary>
    private static HashSet<(string Schema, string Table)> CapturedTables(
        InterchangeSnapshot snapshot,
        IReadOnlyDictionary<ObjectRef, string> schemaNames)
    {
        var set = new HashSet<(string, string)>();
        foreach (var table in snapshot.Tables)
        {
            set.Add((ForeignKeyEdge.SchemaOf(table, schemaNames), table.Name));
        }

        return set;
    }

    /// <summary>
    /// Builds one edge per captured constraint, ordered by child, then parent, then constraint name
    /// (§5.2) — the order the register prints and the cards cite.
    /// </summary>
    private static List<ForeignKeyEdge> BuildEdges(
        InterchangeSnapshot snapshot,
        IReadOnlyDictionary<ObjectRef, string> schemaNames,
        HashSet<(string Schema, string Table)> captured)
    {
        var edges = new List<ForeignKeyEdge>(snapshot.Tables.Sum(t => t.ForeignKeys.Count));

        foreach (var table in snapshot.Tables)
        {
            var childSchema = ForeignKeyEdge.SchemaOf(table, schemaNames);

            foreach (var foreignKey in table.ForeignKeys)
            {
                var resolved = ForeignKeyEdge.TryResolveTable(
                    foreignKey.ReferencedTableRef,
                    out var parentSchema,
                    out var parentTable);

                edges.Add(new ForeignKeyEdge(
                    childSchema,
                    table.Name,
                    resolved ? parentSchema : string.Empty,
                    resolved ? parentTable : string.Empty,
                    foreignKey.Columns,
                    foreignKey.ReferencedColumns,
                    foreignKey.Name,
                    foreignKey.OnDelete,
                    foreignKey.OnUpdate,
                    IsRequired(table, foreignKey.Columns),
                    resolved && captured.Contains((parentSchema, parentTable))));
            }
        }

        return edges
            .OrderBy(e => e.ChildSchema, StringComparer.Ordinal)
            .ThenBy(e => e.ChildTable, StringComparer.Ordinal)
            .ThenBy(e => e.ParentSchema, StringComparer.Ordinal)
            .ThenBy(e => e.ParentTable, StringComparer.Ordinal)
            .ThenBy(e => e.Constraint, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A constraint is required when every referencing column is non-nullable (§5.1). A column the
    /// snapshot does not hold cannot prove nullability, so such a constraint is not claimed as required.
    /// </summary>
    private static bool IsRequired(TableDefinition table, IReadOnlyList<string> columns)
    {
        if (columns.Count == 0)
        {
            return false;
        }

        return columns.All(name => table.Columns.Any(c =>
            string.Equals(c.Name, name, StringComparison.Ordinal) && !c.Nullable));
    }

    /// <summary>
    /// Groups the tables that take part in a relation into the connected components of the FK graph read
    /// as undirected over captured tables only (§5.7). An edge to a table the catalog does not hold is
    /// incident to its child and to nothing else, so it neither merges two groups nor creates one.
    /// </summary>
    private static List<RelationGroup> BuildGroups(IReadOnlyList<ForeignKeyEdge> edges)
    {
        var related = RelatedTables(edges);
        var root = related.ToDictionary(t => t, t => t);

        foreach (var edge in edges)
        {
            // Only an edge with both endpoints captured joins two tables. A self loop joins a table to
            // itself, which is why §5.5's rule needs no filter: such an edge cannot join two components.
            if (edge.ParentIsCaptured && !edge.IsSelfReference)
            {
                Union(root, (edge.ChildSchema, edge.ChildTable), (edge.ParentSchema, edge.ParentTable));
            }
        }

        // Deterministic numbering: components are ordered by their lowest member name ordinal (§5.7),
        // so the group a reader saw as "Group 3" last week is Group 3 again this week.
        var components = related
            .GroupBy(t => Find(root, t))
            .Select(g => g.Select(MemberName).Order(StringComparer.Ordinal).ToList())
            .OrderBy(c => string.Join("\u0001", c), StringComparer.Ordinal)
            .ToList();

        var groups = new List<RelationGroup>(components.Count);
        for (var i = 0; i < components.Count; i++)
        {
            var members = components[i].ToHashSet(StringComparer.Ordinal);
            groups.Add(new RelationGroup(
                i + 1,
                [.. components[i]],
                [.. edges.Where(e => members.Contains(MemberName((e.ChildSchema, e.ChildTable))))]));
        }

        return groups;
    }

    /// <summary>
    /// The tables holding at least one relation: every child of a captured constraint, plus every parent
    /// the catalog itself holds — §5.3's "one heading per table holding at least one relation".
    /// </summary>
    private static List<(string Schema, string Table)> RelatedTables(IReadOnlyList<ForeignKeyEdge> edges)
    {
        var related = new HashSet<(string, string)>();
        foreach (var edge in edges)
        {
            related.Add((edge.ChildSchema, edge.ChildTable));
            if (edge.ParentIsCaptured)
            {
                related.Add((edge.ParentSchema, edge.ParentTable));
            }
        }

        return related.ToList();
    }

    /// <summary>
    /// Builds one card per related table, listing every constraint that touches it in both directions
    /// (§5.3). A self reference appears once as an outgoing and once as an incoming relation, which is
    /// what §5.9's "two card lines per constraint" counts.
    /// </summary>
    private static List<TableCard> BuildCards(
        IReadOnlyList<ForeignKeyEdge> edges,
        IReadOnlyList<RelationMap.CyclicGroup> cyclicGroups)
    {
        var groupOfMember = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in cyclicGroups)
        {
            foreach (var member in group.Members)
            {
                groupOfMember[member] = group.Number;
            }
        }

        return RelatedTables(edges)
            .Select(key => new TableCard(
                key.Schema,
                key.Table,
                [.. edges.Where(e => (e.ChildSchema, e.ChildTable) == key)],
                [.. edges.Where(e => e.ParentIsCaptured && (e.ParentSchema, e.ParentTable) == key)],
                groupOfMember.TryGetValue(MemberName(key), out var group) ? group : null))
            .OrderByDescending(c => c.RelationCount)
            .ThenBy(c => c.QualifiedName, StringComparer.Ordinal)
            .ToList();
    }

    private static CouplingReport BuildCoupling(
        IReadOnlyList<ForeignKeyEdge> edges,
        IReadOnlyList<TableCard> cards,
        HashSet<(string Schema, string Table)> captured)
    {
        var flows = edges
            .Where(e => e.ParentSchema.Length > 0
                        && !string.Equals(e.ChildSchema, e.ParentSchema, StringComparison.Ordinal))
            .GroupBy(e => (From: e.ChildSchema, To: e.ParentSchema))
            .Select(g => new CouplingReport.SchemaFlow(g.Key.From, g.Key.To, g.Count()))
            .OrderBy(f => f.From, StringComparer.Ordinal)
            .ThenBy(f => f.To, StringComparer.Ordinal)
            .ToList();

        // §5.4's ratio is about declaring: a lookup table referenced by 61 constraints still carries no
        // foreign key of its own. §5.8's list is the wider question of taking part in nothing at all.
        var declaring = edges.Select(e => (e.ChildSchema, e.ChildTable)).ToHashSet();

        return new CouplingReport
        {
            FanIn = Rank(cards.Select(c => (c.QualifiedName, c.Incoming.Count))),
            FanOut = Rank(cards.Select(c => (c.QualifiedName, c.Outgoing.Count))),
            DuplicatePairs = DuplicatePairs(edges),
            CrossSchema = flows,
            CrossSchemaTotal = flows.Sum(f => f.Count),
            TablesWithForeignKeys = declaring.Count,
            TablesWithoutForeignKeys = captured.Count - declaring.Count,
        };
    }

    /// <summary>
    /// Pairs joined by two or more constraints, named once each (§5.4). Only pairs whose both endpoints
    /// the catalog holds are measured; an edge leaving the catalog is owned by §5.8's external list.
    /// </summary>
    private static IReadOnlyList<CouplingReport.DuplicatePair> DuplicatePairs(
        IReadOnlyList<ForeignKeyEdge> edges) =>
        [.. edges
            .Where(e => e.CapturedPair is not null)
            .GroupBy(e => e.CapturedPair!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => new CouplingReport.DuplicatePair(
                g.Key.Left,
                g.Key.Right,
                g.Count(),
                [.. g.Select(e => e.Constraint).Order(StringComparer.Ordinal)]))
            .OrderBy(p => p.Left, StringComparer.Ordinal)
            .ThenBy(p => p.Right, StringComparer.Ordinal)];

    /// <summary>
    /// Ranks a degree listing: count descending, name ascending, bounded by
    /// <see cref="ReportBudget.MaxCouplingRows"/>. The bound limits a ranking only — the statistics table
    /// prints the same counts for every table, so no fact lives solely here (§5.4).
    /// </summary>
    private static IReadOnlyList<CouplingReport.Degree> Rank(IEnumerable<(string Table, int Count)> degrees) =>
        [.. degrees
            .Where(d => d.Count > 0)
            .OrderByDescending(d => d.Count)
            .ThenBy(d => d.Table, StringComparer.Ordinal)
            .Take(ReportBudget.MaxCouplingRows)
            .Select(d => new CouplingReport.Degree(d.Table, d.Count))];

    /// <summary>
    /// Finds the strongly-connected components of two or more tables (§5.5). Tarjan's pass returns
    /// <em>components</em>, not cycles — a component of six tables usually holds many — so each one is
    /// reported with its member set, its internal constraint count and one labelled example path.
    /// </summary>
    private static List<RelationMap.CyclicGroup> FindCyclicGroups(IReadOnlyList<ForeignKeyEdge> edges)
    {
        var successors = BuildSuccessors(edges);
        var components = StronglyConnectedComponents(successors);

        var cyclic = components
            .Where(c => c.Count > 1)
            .Select(c => Describe(c, edges, successors))
            .OrderBy(g => string.Join("\u0001", g.Members), StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < cyclic.Count; i++)
        {
            cyclic[i] = cyclic[i] with { Number = i + 1 };
        }

        return cyclic;
    }

    private static RelationMap.CyclicGroup Describe(
        List<string> component,
        IReadOnlyList<ForeignKeyEdge> edges,
        Dictionary<string, List<string>> successors)
    {
        var members = component.Order(StringComparer.Ordinal).ToList();
        var memberSet = members.ToHashSet(StringComparer.Ordinal);

        // Self loops are §5.8's exclusively, so they are not counted among a component's own constraints.
        List<string> internalConstraints = [.. edges
            .Where(e => e.ParentIsCaptured
                        && !e.IsSelfReference
                        && memberSet.Contains(e.ChildName)
                        && memberSet.Contains(e.ParentName))
            .Select(e => e.Constraint)];

        var path = FindRepresentativePath(members, memberSet, successors);

        return new RelationMap.CyclicGroup(
            0,
            members,
            internalConstraints,
            path.Tables,
            path.ConstraintCount);
    }

    /// <summary>Directed adjacency over captured tables only, self loops excluded (§5.5).</summary>
    private static Dictionary<string, List<string>> BuildSuccessors(IReadOnlyList<ForeignKeyEdge> edges)
    {
        var successors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in edges.Where(e => e.ParentIsCaptured && !e.IsSelfReference))
        {
            if (!successors.TryGetValue(edge.ChildName, out var children))
            {
                children = successors[edge.ChildName] = [];
            }

            if (!children.Contains(edge.ParentName))
            {
                children.Add(edge.ParentName);
            }
        }

        foreach (var list in successors.Values)
        {
            list.Sort(StringComparer.Ordinal);
        }

        return successors;
    }

    /// <summary>
    /// Tarjan's algorithm, iterated: a catalog may hold a reference chain longer than a recursion would
    /// survive, and a human-readable courtesy document is not worth a stack overflow.
    /// </summary>
    private static List<List<string>> StronglyConnectedComponents(
        Dictionary<string, List<string>> successors)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLink = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var components = new List<List<string>>();
        var counter = 0;

        // Nodes are entered in ordinal order, so the components a snapshot yields do not depend on the
        // order its tables were inserted in (§7: components identical across two builds).
        var nodes = successors.Keys.Order(StringComparer.Ordinal).ToList();

        foreach (var start in nodes)
        {
            if (index.ContainsKey(start))
            {
                continue;
            }

            var walk = new Stack<(string Node, List<string> Children, int Next)>();
            Visit(start);
            walk.Push((start, Neighbours(successors, start), 0));

            while (walk.Count > 0)
            {
                var frame = walk.Pop();

                if (frame.Next < frame.Children.Count)
                {
                    var child = frame.Children[frame.Next];
                    walk.Push((frame.Node, frame.Children, frame.Next + 1));

                    if (!index.ContainsKey(child))
                    {
                        Visit(child);
                        walk.Push((child, Neighbours(successors, child), 0));
                    }
                    else if (onStack.Contains(child))
                    {
                        lowLink[frame.Node] = Math.Min(lowLink[frame.Node], index[child]);
                    }

                    continue;
                }

                if (walk.TryPeek(out var parent))
                {
                    lowLink[parent.Node] = Math.Min(lowLink[parent.Node], lowLink[frame.Node]);
                }

                if (lowLink[frame.Node] != index[frame.Node])
                {
                    continue;
                }

                var component = new List<string>();
                while (stack.TryPop(out var member))
                {
                    onStack.Remove(member);
                    component.Add(member);
                    if (string.Equals(member, frame.Node, StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                components.Add(component);
            }
        }

        return components;

        void Visit(string node)
        {
            index[node] = counter;
            lowLink[node] = counter;
            counter++;
            stack.Push(node);
            onStack.Add(node);
        }
    }

    private static List<string> Neighbours(Dictionary<string, List<string>> successors, string node) =>
        successors.TryGetValue(node, out var found) ? found : [];

    /// <summary>
    /// One representative cycle, chosen deterministically: the earliest member that has one, and the
    /// shortest path back to itself through the component. It is an example of the entanglement, never a
    /// statement that the entanglement is this long (§5.5).
    /// </summary>
    private static (List<string> Tables, int ConstraintCount) FindRepresentativePath(
        List<string> members,
        HashSet<string> memberSet,
        Dictionary<string, List<string>> successors)
    {
        foreach (var start in members)
        {
            var cycle = ShortestCycle(start, memberSet, successors);
            if (cycle is not null)
            {
                return (cycle, cycle.Count - 1);
            }
        }

        // A strongly-connected component of two or more tables always has a cycle, so this line is
        // unreachable; the fallback keeps the reported path well-formed rather than empty.
        return (new List<string> { members[0], members[0] }, 1);
    }

    private static List<string>? ShortestCycle(
        string start,
        HashSet<string> memberSet,
        Dictionary<string, List<string>> successors)
    {
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var first in Neighbours(successors, start))
        {
            if (!memberSet.Contains(first) || !seen.Add(first))
            {
                continue;
            }

            previous[first] = start;
            queue.Enqueue(first);
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var next in Neighbours(successors, node))
            {
                if (!memberSet.Contains(next))
                {
                    continue;
                }

                if (string.Equals(next, start, StringComparison.Ordinal))
                {
                    return ClosePath(start, node, previous);
                }

                if (seen.Add(next))
                {
                    previous[next] = node;
                    queue.Enqueue(next);
                }
            }
        }

        return null;
    }

    private static List<string> ClosePath(string start, string end, Dictionary<string, string> previous)
    {
        var path = new List<string>();
        for (var node = end; previous.TryGetValue(node, out var parent); node = parent)
        {
            path.Add(node);
            if (string.Equals(parent, start, StringComparison.Ordinal))
            {
                break;
            }
        }

        path.Reverse();
        path.Insert(0, start);
        path.Add(start);
        return path;
    }

    /// <summary>
    /// The §5.8 list: tables taking part in no foreign-key relationship at all, in either direction.
    /// Worded as what was checked — such a table may still be read by views, routines or application
    /// code, so the report never calls it unrelated.
    /// </summary>
    private static IReadOnlyList<string> TablesWithNoRelations(
        IReadOnlyList<TableCard> cards,
        HashSet<(string Schema, string Table)> captured)
    {
        var related = cards.Select(c => MemberName((c.Schema, c.Table))).ToHashSet(StringComparer.Ordinal);
        return [.. captured
            .Select(key => MemberName(key))
            .Where(name => !related.Contains(name))
            .Order(StringComparer.Ordinal)];
    }

    private static IReadOnlyList<RelationMap.LineagePair> BuildLineage(InterchangeSnapshot snapshot) =>
        [.. snapshot.Relationships
            .Where(r => string.Equals(r.Type, ViewReferenceType, StringComparison.Ordinal))
            .Select(r => new RelationMap.LineagePair(
                ForeignKeyEdge.QualifiedNameOf(r.DependentRef),
                ForeignKeyEdge.QualifiedNameOf(r.DependencyRef)))
            .OrderBy(p => p.Dependent, StringComparer.Ordinal)
            .ThenBy(p => p.Target, StringComparer.Ordinal)];

    private static string MemberName((string Schema, string Table) key) =>
        ReportText.Qualified(key.Schema, key.Table);

    private static (string Schema, string Table) Find(
        Dictionary<(string Schema, string Table), (string Schema, string Table)> root,
        (string Schema, string Table) node)
    {
        var current = node;
        while (!EqualityComparer<(string, string)>.Default.Equals(root[current], current))
        {
            current = root[current];
        }

        return current;
    }

    private static void Union(
        Dictionary<(string Schema, string Table), (string Schema, string Table)> root,
        (string Schema, string Table) left,
        (string Schema, string Table) right)
    {
        var leftRoot = Find(root, left);
        var rightRoot = Find(root, right);
        if (!EqualityComparer<(string, string)>.Default.Equals(leftRoot, rightRoot))
        {
            root[rightRoot] = leftRoot;
        }
    }
}
