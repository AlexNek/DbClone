using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The arrangement a hub is read in: the table most of the catalog names stands in the middle, the
/// tables it names stand in the ring around it, and every further table stands in the wedge of the
/// circle its own parent was handed.
/// </summary>
/// <remarks>
/// <para>
/// A catalog whose relations mostly meet one table, or whose families branch out of one root, has no top
/// to read down from — one table is simply what the others point at — and the space rings leave between
/// the cards is what lets an arrow be drawn straight. Bands pack the same cards side by side in a lattice,
/// and a card standing between an arrow's two tables is what forces that arrow to turn in a corridor. So
/// the two are drawn and measured and the cheaper picture kept: the bands stay wherever they draw every
/// arrow straight, which is where their rule that a table stand under the table it names costs nothing
/// (T5, T25, T26).
/// </para>
/// <para>
/// A wedge is handed down, not spread over the whole circle: a table owns as much of the ring as the
/// tables below it need, and its own children divide that share between them. That is what keeps a
/// family's arrows inside one slice of the picture instead of crossing it — two tables whose arrows
/// would otherwise be drawn along the same stroke are separated by the wedges they stand in, because
/// a wedge is wide enough for the cards it holds and no wider. The room a card needs on a ring is
/// measured on the ring itself: an arc of <c>radius x wedge</c> has to hold the card and the air
/// beside it, which is what sets how far out the ring stands.
/// </para>
/// <para>
/// A cyclic group is handed one wedge as one unit, and its members divide it in the order they point at
/// one another. Reaching a table by the shortest step from the middle, which is what the rings are
/// otherwise built from, would put two tables of one cycle on different rings and the arrow closing the
/// cycle across everything between them; standing the whole group on one ring in its own order makes
/// every arrow of the cycle the short chord between neighbours, and no chord of it crosses another.
/// </para>
/// <para>
/// Nothing here is a rule a catalog has to fit. Rings hand one circle out around one middle, so a catalog
/// that is several pictures — families that share no constraint, a table nobody joins and nobody names —
/// is grouped into the pieces it actually has, each drawn on its own middle and then stood beside the
/// others. A table joined to nothing is not given the place a family would have had, and the arrow that
/// closes a ring is not bowed around a card that has no part in it.
/// </para>
/// <para>
/// The answer is a plain function of the graph, so the same catalog is arranged the same way on every
/// run, under any culture. Nothing here draws an arrow: <see cref="GraphChordRouter"/> routes the
/// picture this places.
/// </para>
/// </remarks>
internal static class GraphWedgePlacement
{
    /// <summary>How far out the next ring stands from the ring before it.</summary>
    internal const double RingStep = 170;

    /// <summary>The least air one card needs beside its neighbour on the same ring.</summary>
    internal const double ArcAir = 40;

    /// <summary>The air left between two pieces of a catalog that share no constraint, so they read as two pictures.</summary>
    private const double ComponentGap = 80;

    /// <summary>The narrowest wedge a card is given, so a deep leaf is never placed on its parent's line.</summary>
    private const double NarrowestWedge = 0.02;

    /// <summary>How many times a ring is pushed out when two of its cards lean on a card of another ring.</summary>
    private const int Growths = 8;

    /// <summary>How far a ring moves out in one growth.</summary>
    private const double Growth = 90;

    /// <summary>Places every card of the picture on the ring and in the wedge its family owns.</summary>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="widths">The measured width of every card, in node order.</param>
    /// <returns>One placement per node and the picture's own size, margins included.</returns>
    internal static (NodePlacement[] Placement, double Width, double Height) Place(
        CatalogGraphModel model,
        IReadOnlyList<double> widths)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(widths);

        var units = Units(model, out var unitOfNode);
        var joined = JoinedUnits(model, units, unitOfNode);
        var (names, namedBy) = Naming(model, units, unitOfNode);

        // Every piece of the catalog is drawn on its own middle: the grouping is read off the constraints,
        // so a database that is one family comes out as one circle and one that is six unrelated families
        // comes out as six, without either answer being a rule the catalog has to fit.
        var pieces = new List<List<NodePlacement>>();
        foreach (var component in Components(units, joined))
        {
            pieces.Add(Piece(model, units, joined, names, namedBy, widths, component));
        }

        return Pack(pieces, model.Nodes.Count);
    }

    /// <summary>
    /// The groups of units that reach one another through a constraint, in the order their least unit
    /// stands in the catalog. A unit joined to nothing is a group of one: it belongs to no family and is
    /// given a picture of its own rather than a place inside somebody else's ring.
    /// </summary>
    private static List<List<int>> Components(List<List<int>> units, HashSet<int>[] joined)
    {
        var components = new List<List<int>>();
        var seen = new bool[units.Count];

        for (var start = 0; start < units.Count; start++)
        {
            if (seen[start])
            {
                continue;
            }

            var group = new List<int>();
            var frontier = new List<int> { start };
            seen[start] = true;

            while (frontier.Count > 0)
            {
                var current = frontier[^1];
                frontier.RemoveAt(frontier.Count - 1);
                group.Add(current);

                foreach (var other in joined[current])
                {
                    if (!seen[other])
                    {
                        seen[other] = true;
                        frontier.Add(other);
                    }
                }
            }

            group.Sort();
            components.Add(group);
        }

        return components;
    }

    /// <summary>
    /// One piece of the catalog, drawn at the origin: the rings, the wedges and the middle all belong to
    /// this piece alone, so nothing a constraint never reaches stands in the place a family would have had.
    /// </summary>
    private static List<NodePlacement> Piece(
        CatalogGraphModel model,
        List<List<int>> units,
        HashSet<int>[] joined,
        HashSet<int>[] names,
        int[] namedBy,
        IReadOnlyList<double> widths,
        IReadOnlyList<int> component)
    {
        var centre = Middle(component, units, joined);
        var tree = Tree(component, centre, joined, names, namedBy);

        // A relation that is not a step of the tree has its two ends anywhere in the wedge their parent
        // owns, and anywhere is where an arrow cuts across the picture. Standing tables that are joined to
        // one another beside one another turns those relations into the short chord between neighbours.
        Order(tree, joined);

        var wedges = Wedges(tree, centre, Weights(tree, units));
        var nodes = Nodes(units, component);
        var (depth, wedgeOfNode) = Expand(model, units, tree, wedges, centre, component);
        var rings = Rings(depth, wedgeOfNode, widths, nodes);

        var cards = new List<NodePlacement>(nodes.Count);
        foreach (var node in nodes)
        {
            var width = widths[node];
            // Only the middle unit is drawn at the centre; a cyclic group holding it stands on the ring
            // around the centre, where its members can be drawn in a ring of their own.
            var (cx, cy) = rings[depth[node]] == 0
                ? (0d, 0d)
                : Point(rings[depth[node]], (wedgeOfNode[node].From + wedgeOfNode[node].To) / 2d);

            cards.Add(new NodePlacement(node, new GraphPoint(cx - (width / 2), cy - (GraphCard.Height / 2)),
                width, GraphCard.Height));
        }

        return cards;
    }

    /// <summary>The tables a group of units holds, in node order.</summary>
    private static List<int> Nodes(List<List<int>> units, IReadOnlyList<int> component)
    {
        var nodes = new List<int>();
        foreach (var unit in component)
        {
            nodes.AddRange(units[unit]);
        }

        nodes.Sort();
        return nodes;
    }

    /// <summary>
    /// The pieces stood beside one another. How many go in a line is measured rather than fixed: the line
    /// length that leaves the smallest page is kept, and a tie falls to the more square of the two, so a
    /// catalog of six unrelated families is drawn in two lines of three rather than a strip six wide or a
    /// column six tall, and a catalog that is one family is drawn as the single picture it is.
    /// </summary>
    private static (NodePlacement[] Placement, double Width, double Height) Pack(
        List<List<NodePlacement>> pieces,
        int nodes)
    {
        var boxes = pieces.Select(OwnBox).ToList();
        var order = Enumerable.Range(0, pieces.Count)
            .OrderByDescending(index => boxes[index].Width * boxes[index].Height)
            .ThenBy(index => index)
            .ToList();

        var best = LineUp(order, boxes, 1);
        var bestScore = (best.Width * best.Height, Math.Max(best.Width, best.Height));

        for (var columns = 2; columns <= order.Count; columns++)
        {
            var candidate = LineUp(order, boxes, columns);
            var score = (candidate.Width * candidate.Height, Math.Max(candidate.Width, candidate.Height));
            if (score.CompareTo(bestScore) < 0)
            {
                (best, bestScore) = (candidate, score);
            }
        }

        var placed = new NodePlacement[nodes];
        for (var index = 0; index < pieces.Count; index++)
        {
            var (at, left, top) = (best.At[index], pieces[index].Min(card => card.TopLeft.X), pieces[index].Min(card => card.TopLeft.Y));
            foreach (var card in pieces[index])
            {
                placed[card.NodeIndex] = card with
                {
                    TopLeft = new GraphPoint(at.X + (card.TopLeft.X - left), at.Y + (card.TopLeft.Y - top)),
                };
            }
        }

        return Shift(placed);
    }

    /// <summary>The ground one piece covers, measured on its own cards.</summary>
    private static (double Width, double Height) OwnBox(IReadOnlyList<NodePlacement> cards)
    {
        var (left, top) = (cards.Min(card => card.TopLeft.X), cards.Min(card => card.TopLeft.Y));
        var (right, bottom) = (cards.Max(card => card.Right), cards.Max(card => card.Bottom));

        return (right - left, bottom - top);
    }

    /// <summary>The page one line length gives, and where each piece's own box starts on it.</summary>
    private static Layout LineUp(IReadOnlyList<int> order, IReadOnlyList<(double Width, double Height)> boxes, int columns)
    {
        var at = new Dictionary<int, GraphPoint>();
        var (left, top, tallest, widest) = (0d, 0d, 0d, 0d);
        var taken = 0;

        foreach (var index in order)
        {
            if (taken == columns)
            {
                widest = Math.Max(widest, left - ComponentGap);
                top += tallest + ComponentGap;
                (left, tallest, taken) = (0d, 0d, 0);
            }

            at[index] = new GraphPoint(left, top);
            left += boxes[index].Width + ComponentGap;
            tallest = Math.Max(tallest, boxes[index].Height);
            taken++;
        }

        return new Layout(Math.Max(widest, left - ComponentGap), top + tallest, at);
    }

    /// <summary>
    /// The units the circle is handed out in: every cyclic group of two or more tables as one unit, and
    /// every other table as a unit of its own. A group is one unit because its members reach each other —
    /// no one of them stands behind another — and because their arrows are only straight when the members
    /// stand beside one another on one ring, in the order they point.
    /// </summary>
    private static List<List<int>> Units(CatalogGraphModel model, out int[] unitOfNode)
    {
        unitOfNode = new int[model.Nodes.Count];
        Array.Fill(unitOfNode, -1);
        var units = new List<List<int>>();

        foreach (var group in model.CyclicGroups)
        {
            if (group.MemberNodeIndexes.Count < 2)
            {
                continue;
            }

            var members = group.MemberNodeIndexes.ToList();
            members.Sort();
            Claim(units, unitOfNode, members);
        }

        for (var node = 0; node < model.Nodes.Count; node++)
        {
            if (unitOfNode[node] < 0)
            {
                Claim(units, unitOfNode, [node]);
            }
        }

        return units;
    }

    private static void Claim(List<List<int>> units, int[] unitOfNode, List<int> members)
    {
        var index = units.Count;
        units.Add(members);
        foreach (var member in members)
        {
            unitOfNode[member] = index;
        }
    }

    /// <summary>The units each unit is joined to, in either direction: a step between units, never inside one.</summary>
    private static HashSet<int>[] JoinedUnits(CatalogGraphModel model, List<List<int>> units, int[] unitOfNode)
    {
        var joined = new HashSet<int>[units.Count];
        for (var unit = 0; unit < joined.Length; unit++)
        {
            joined[unit] = [];
        }

        foreach (var edge in model.Edges)
        {
            var (one, other) = (unitOfNode[edge.ChildIndex], unitOfNode[edge.ParentIndex]);
            if (one == other)
            {
                continue;
            }

            joined[one].Add(other);
            joined[other].Add(one);
        }

        return joined;
    }

    /// <summary>
    /// Which units each unit names with its own constraints, and how many units name each unit. The two are
    /// read together: a table the whole catalog names is the ground every family stands on, and a table few
    /// name is a family of its own.
    /// </summary>
    private static (HashSet<int>[] Names, int[] NamedBy) Naming(
        CatalogGraphModel model,
        List<List<int>> units,
        int[] unitOfNode)
    {
        var names = new HashSet<int>[units.Count];
        for (var unit = 0; unit < names.Length; unit++)
        {
            names[unit] = [];
        }

        foreach (var edge in model.Edges)
        {
            var (child, parent) = (unitOfNode[edge.ChildIndex], unitOfNode[edge.ParentIndex]);
            if (child != parent)
            {
                names[child].Add(parent);
            }
        }

        var namedBy = new int[units.Count];
        for (var unit = 0; unit < names.Length; unit++)
        {
            foreach (var named in names[unit])
            {
                namedBy[named]++;
            }
        }

        return (names, namedBy);
    }

    /// <summary>The unit the rings are drawn around: the one the most other tables are joined to.</summary>
    private static int Middle(IReadOnlyList<int> component, List<List<int>> units, HashSet<int>[] joined)
    {
        var middle = component[0];
        for (var step = 1; step < component.Count; step++)
        {
            if (Reach(joined, units, component[step]) > Reach(joined, units, middle))
            {
                middle = component[step];
            }
        }

        return middle;
    }

    /// <summary>How many tables stand outside a unit and are joined to it.</summary>
    private static int Reach(HashSet<int>[] joined, List<List<int>> units, int unit) =>
        joined[unit].Sum(other => units[other].Count);

    /// <summary>
    /// The rings and the parents they hang from, for the units of one piece. Every unit stands behind the
    /// one it is most specifically joined to — a unit it names rather than one that names it, and of those
    /// the one the fewest others name — and the ring it stands on is as far out as that chain of parents
    /// reaches from the middle. The chain is taken whole rather than grown step by step, because a table
    /// claimed by the middle before its own family is standing is drawn beside the family instead of inside
    /// it, and then the relation that joins them is a chord drawn across the arrows of the tables standing
    /// between them. A chain that meets no standing unit — one that loops, or one that runs off to a table
    /// naming nothing — is drawn on the ring beside the middle, because a picture that leaves a card out
    /// cannot be put back.
    /// </summary>
    private static Forest Tree(
        IReadOnlyList<int> members,
        int centre,
        HashSet<int>[] joined,
        HashSet<int>[] names,
        int[] namedBy)
    {
        var depth = new int[joined.Length];
        Array.Fill(depth, -1);
        var kids = new List<int>[joined.Length];
        for (var unit = 0; unit < kids.Length; unit++)
        {
            kids[unit] = [];
        }

        var parent = IdealParent(members, joined, names, namedBy);
        depth[centre] = 0;
        parent[centre] = -1;

        foreach (var unit in members)
        {
            if (depth[unit] >= 0)
            {
                continue;
            }

            var chain = Chain(unit, parent, depth, out var reached);
            var ring = reached >= 0 ? depth[reached] : 0;
            var under = reached;

            for (var step = chain.Count - 1; step >= 0; step--)
            {
                var member = chain[step];
                ring = reached >= 0 ? ring + 1 : 1;
                depth[member] = ring;
                kids[reached >= 0 ? under : centre].Add(member);
                under = member;
            }
        }

        return new Forest(depth, kids);
    }

    /// <summary>The unit each unit stands behind: the one it is most specifically joined to.</summary>
    private static int[] IdealParent(
        IReadOnlyList<int> members,
        HashSet<int>[] joined,
        HashSet<int>[] names,
        int[] namedBy)
    {
        var parent = new int[joined.Length];

        foreach (var unit in members)
        {
            var best = -1;
            var bestKey = (int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);
            foreach (var other in joined[unit])
            {
                var key = Key(unit, other, names, namedBy);
                if (key.CompareTo(bestKey) < 0)
                {
                    bestKey = key;
                    best = other;
                }
            }

            parent[unit] = best;
        }

        return parent;
    }

    /// <summary>
    /// One unit and the parents behind it, walked inward until a unit already standing is met — which is
    /// reported as <c>reached</c>, or <c>-1</c> when the chain loops back on itself or runs out instead.
    /// </summary>
    private static List<int> Chain(int unit, int[] parent, int[] depth, out int reached)
    {
        var chain = new List<int>();
        var seen = new HashSet<int>();
        var current = unit;
        reached = -1;

        while (current >= 0 && seen.Add(current))
        {
            if (depth[current] >= 0)
            {
                reached = current;
                break;
            }

            chain.Add(current);
            current = parent[current];
        }

        return chain;
    }

    /// <summary>
    /// How well one unit serves as the parent of another: naming beats being named, a table few others name
    /// beats one everybody names, and the canonical order settles the rest, so the same catalog is arranged
    /// the same way on every run, under any culture.
    /// </summary>
    private static (int Direction, int Specific, int Parent, int Unit) Key(
        int unit,
        int parent,
        HashSet<int>[] names,
        int[] namedBy) =>
        (names[unit].Contains(parent) ? 0 : 1, namedBy[parent], parent, unit);

    /// <summary>How many tables each unit carries, its own members and its whole family included.</summary>
    private static int[] Weights(Forest tree, List<List<int>> units)
    {
        var deepest = tree.Depth.Max();
        var weight = new int[tree.Depth.Length];
        for (var unit = 0; unit < weight.Length; unit++)
        {
            weight[unit] = units[unit].Count;
        }

        for (var ring = deepest; ring >= 1; ring--)
        {
            for (var unit = 0; unit < tree.Depth.Length; unit++)
            {
                if (tree.Depth[unit] != ring - 1)
                {
                    continue;
                }

                weight[unit] += tree.Kids[unit].Sum(child => weight[child]);
            }
        }

        return weight;
    }

    /// <summary>
    /// Divides the circle: the middle owns all of it, and every table owns the share of its parent's
    /// wedge its own family needs. Children keep their canonical order round the wedge, so the same
    /// catalog is drawn the same way twice.
    /// </summary>
    private static (double From, double To)[] Wedges(Forest tree, int centre, int[] weight)
    {
        var wedges = new (double From, double To)[tree.Depth.Length];
        wedges[centre] = (-Math.PI / 2, (-Math.PI / 2) + (2 * Math.PI));

        var frontier = new[] { centre };
        while (frontier.Length > 0)
        {
            var next = new List<int>();
            foreach (var node in frontier)
            {
                var own = wedges[node];
                var share = own.To - own.From;
                var family = tree.Kids[node].Sum(child => weight[child]);

                if (family == 0)
                {
                    continue;
                }

                var cursor = own.From;
                foreach (var child in tree.Kids[node])
                {
                    var width = share * weight[child] / family;
                    wedges[child] = (cursor, cursor + width);
                    cursor += width;
                    next.Add(child);
                }
            }

            frontier = [.. next];
        }

        for (var node = 0; node < wedges.Length; node++)
        {
            if (node != centre && wedges[node].To - wedges[node].From < NarrowestWedge)
            {
                var middle = (wedges[node].From + wedges[node].To) / 2d;
                wedges[node] = (middle - (NarrowestWedge / 2), middle + (NarrowestWedge / 2));
            }
        }

        return wedges;
    }

    /// <summary>
    /// The order the tables of a wedge stand in: every table that is joined to another table of the same
    /// wedge takes that table's place in the order, so a group of tables joined to one another is drawn
    /// as one block rather than scattered round the circle. Ties fall back to the unit's own number, so
    /// the same catalog is arranged the same way on every run, under any culture.
    /// </summary>
    private static void Order(Forest tree, HashSet<int>[] joined)
    {
        var parent = ParentOf(tree);
        for (var unit = 0; unit < tree.Kids.Length; unit++)
        {
            if (tree.Kids[unit].Count < 2)
            {
                continue;
            }

            var rank = tree.Kids[unit].ToDictionary(child => child, child => Block(child, unit, joined, parent));
            tree.Kids[unit] = [.. tree.Kids[unit].OrderBy(child => rank[child]).ThenBy(child => child)];
        }
    }

    /// <summary>The unit every unit was first reached from; the middle's own place is empty.</summary>
    private static int[] ParentOf(Forest tree)
    {
        var parent = new int[tree.Kids.Length];
        Array.Fill(parent, -1);
        for (var unit = 0; unit < tree.Kids.Length; unit++)
        {
            foreach (var child in tree.Kids[unit])
            {
                parent[child] = unit;
            }
        }

        return parent;
    }

    /// <summary>
    /// The place in the wedge a unit stands at: the lowest of itself and the tables beside it that it is
    /// joined to. Two tables joined to one another are therefore given the same place and stand side by
    /// side, and the arrow between them never has to reach over the tables in between.
    /// </summary>
    private static int Block(int child, int parent, HashSet<int>[] joined, int[] parentOf)
    {
        var block = child;
        foreach (var other in joined[child].OrderBy(value => value))
        {
            if (other != parent && parentOf[other] == parent && other < block)
            {
                block = other;
            }
        }

        return block;
    }

    /// <summary>
    /// The ring and the arc of every table. A unit of one table takes its unit's ring and its unit's wedge;
    /// a cyclic group divides its wedge among its members in the order they point at one another, so the
    /// arrows of the cycle are the short chords between neighbours on one ring rather than crossings over
    /// it. A group that holds the middle stands on the first ring and every other unit moves one ring out,
    /// because nothing else may take the centre's place.
    /// </summary>
    private static (int[] Depth, (double From, double To)[] Wedge) Expand(
        CatalogGraphModel model,
        List<List<int>> units,
        Forest tree,
        (double From, double To)[] wedges,
        int centre,
        IReadOnlyList<int> component)
    {
        var depth = new int[model.Nodes.Count];
        var wedge = new (double From, double To)[model.Nodes.Count];
        var offset = units[centre].Count > 1 ? 1 : 0;

        foreach (var unit in component)
        {
            var members = CycleOrder(model, units[unit]);
            var own = wedges[unit];
            // A member is never given less arc than one card needs, even when the wedge its group was
            // handed is narrower than the group: the ring is then pushed out rather than the cards stacked.
            var share = Math.Max((own.To - own.From) / members.Count, NarrowestWedge);
            var ring = tree.Depth[unit] + offset;

            for (var step = 0; step < members.Count; step++)
            {
                depth[members[step]] = ring;
                wedge[members[step]] = (own.From + (step * share), own.From + ((step + 1) * share));
            }
        }

        return (depth, wedge);
    }

    /// <summary>
    /// The members of one unit in the order they point at one another, so a cycle closes along its own
    /// arc. A group that is not one simple cycle continues from the first member the walk could not
    /// reach, in canonical order, and a unit of one table is its own order.
    /// </summary>
    private static List<int> CycleOrder(CatalogGraphModel model, List<int> members)
    {
        if (members.Count == 1)
        {
            return members;
        }

        var inside = members.ToHashSet();
        var onward = new Dictionary<int, int>();
        foreach (var edge in model.Edges)
        {
            if (edge.ChildIndex != edge.ParentIndex
                && inside.Contains(edge.ChildIndex) && inside.Contains(edge.ParentIndex))
            {
                onward.TryAdd(edge.ChildIndex, edge.ParentIndex);
            }
        }

        var order = new List<int>(members.Count);
        var seen = new HashSet<int>();
        foreach (var start in members)
        {
            for (var current = start; current >= 0 && seen.Add(current); current = After(onward, current))
            {
                order.Add(current);
            }
        }

        return order;
    }

    private static int After(Dictionary<int, int> onward, int current) =>
        onward.TryGetValue(current, out var next) ? next : -1;

    /// <summary>
    /// How far out each ring stands. A ring is placed where every card on it fits along the arc, and
    /// never closer than one <see cref="RingStep"/> than the ring before it; a ring whose cards lean on
    /// a card of the ring inside it is pushed further out until none does, which is the case a leaf
    /// hangs directly behind its own parent.
    /// </summary>
    private static double[] Rings(
        int[] depth,
        (double From, double To)[] wedges,
        IReadOnlyList<double> widths,
        IReadOnlyList<int> nodes)
    {
        var deepest = nodes.Max(node => depth[node]);
        var rings = new double[deepest + 1];
        var byRing = new List<int>[deepest + 1];
        for (var ring = 0; ring <= deepest; ring++)
        {
            byRing[ring] = [];
        }

        foreach (var node in nodes)
        {
            byRing[depth[node]].Add(node);
        }

        for (var ring = 1; ring <= deepest; ring++)
        {
            var need = rings[ring - 1] + RingStep;
            foreach (var node in byRing[ring])
            {
                var span = Math.Max(wedges[node].To - wedges[node].From, NarrowestWedge);
                need = Math.Max(need, (widths[node] + ArcAir) / span);
            }

            rings[ring] = need;
        }

        for (var pass = 0; pass < Growths; pass++)
        {
            var leaning = Leaning(rings, depth, wedges, widths, nodes);
            if (leaning < 0)
            {
                break;
            }

            rings[leaning] += Growth;
        }

        return rings;
    }

    /// <summary>
    /// The outermost ring that has a card standing on another card, or <c>-1</c> when none does. Two
    /// cards of one ring cannot overlap, because the ring is sized for the arcs they hold; a card of a
    /// further ring can stand on its parent's card when its wedge is too narrow to move it aside.
    /// </summary>
    private static int Leaning(
        double[] rings,
        int[] depth,
        (double From, double To)[] wedges,
        IReadOnlyList<double> widths,
        IReadOnlyList<int> nodes)
    {
        var worst = -1;
        var boxes = new (double X, double Y)[depth.Length];
        foreach (var node in nodes)
        {
            boxes[node] = rings[depth[node]] == 0
                ? (0, 0)
                : Point(rings[depth[node]], (wedges[node].From + wedges[node].To) / 2d);
        }

        for (var first = 0; first < nodes.Count; first++)
        {
            for (var second = first + 1; second < nodes.Count; second++)
            {
                var (one, two) = (nodes[first], nodes[second]);
                if (depth[one] == depth[two] || !Overlap(boxes[one], widths[one], boxes[two], widths[two]))
                {
                    continue;
                }

                worst = Math.Max(worst, Math.Max(depth[one], depth[two]));
            }
        }

        return worst;
    }

    /// <summary>Whether two cards occupy the same part of the page.</summary>
    private static bool Overlap((double X, double Y) first, double firstWidth, (double X, double Y) second, double secondWidth)
    {
        const double tolerance = 0.001;

        return Math.Abs(first.X - second.X) < ((firstWidth + secondWidth) / 2) - tolerance
            && Math.Abs(first.Y - second.Y) < GraphCard.Height - tolerance;
    }

    /// <summary>Where a card stands on a ring at the middle angle of its own wedge.</summary>
    private static (double X, double Y) Point(double radius, double angle) =>
        (radius * Math.Cos(angle), radius * Math.Sin(angle));

    /// <summary>Moves the whole picture so its own margin holds it, the way the bands' lattice does.</summary>
    private static (NodePlacement[] Placement, double Width, double Height) Shift(NodePlacement[] placed)
    {
        var left = placed.Min(box => box.TopLeft.X);
        var top = placed.Min(box => box.TopLeft.Y);
        var right = placed.Max(box => box.Right);
        var bottom = placed.Max(box => box.Bottom);
        var (shiftX, shiftY) = (GraphRankedPlacement.Margin - left, GraphRankedPlacement.Margin - top);

        for (var node = 0; node < placed.Length; node++)
        {
            placed[node] = placed[node] with
            {
                TopLeft = new GraphPoint(placed[node].TopLeft.X + shiftX, placed[node].TopLeft.Y + shiftY),
            };
        }

        return (placed, (right - left) + (2 * GraphRankedPlacement.Margin), (bottom - top) + (2 * GraphRankedPlacement.Margin));
    }

    /// <summary>The units and the parents they hang from, as the middle reached them.</summary>
    /// <param name="Depth">The ring every unit stands on.</param>
    /// <param name="Kids">The units each unit reached first.</param>
    private sealed record Forest(int[] Depth, List<int>[] Kids);

    /// <summary>The page one line length gives, and where each piece's own box was put on it.</summary>
    /// <param name="Width">How wide the pieces come out at that many in a line.</param>
    /// <param name="Height">How tall they come out at that many in a line.</param>
    /// <param name="At">The top-left every piece's own box starts at.</param>
    private sealed record Layout(double Width, double Height, Dictionary<int, GraphPoint> At);
}
