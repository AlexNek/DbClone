namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Arranges the cluster boxes in rank bands. Builds the quotient graph, gives every cluster the band its
/// place in the quotient's reference order puts it in — a cluster that is named stands above the clusters
/// that name it, so the picture reads top-down the way a single ranked component does — orders each band by
/// the barycentre of the clusters the band leans on so the relations between bands cross as little as
/// possible, packs each band left to right keeping a full cluster gap between boxes (so two boxes never
/// share ground), slides the band under the clusters it names, picks each box the mirror image that shortens
/// its relations to the other clusters, trades neighbouring boxes within a band while that draws fewer
/// relations across one another, and nudges a box along its band to clear ground for the straight
/// segment between the centres of the cards at each end of a cross-cluster relation.
/// </summary>
internal static class GraphClusterArranger
{
    /// <summary>How many down-and-back barycentre passes settle the order of the bands.</summary>
    private const int Sweeps = 4;
    
    /// <summary>
    /// How many rounds of box slides the ground-clearing makes before it leaves the picture as it stands. Six
    /// rounds were measured on the catalog-shaped component and changed nothing: the third round already
    /// stopped improving, so the descent had converged.
    /// </summary>
    private const int GroundRounds = 3;

    /// <summary>How many sweeps of adjacent swaps the band order is settled by.</summary>
    private const int OrderRounds = 3;

    /// <summary>Arranges cluster boxes in rank bands, parent clusters above the clusters that name them.</summary>
    /// <param name="clustered">The cluster set (node-local labels, members, modularity).</param>
    /// <param name="locals">Each cluster's local layout (nodes, top-lefts, width, height).</param>
    /// <param name="profile">The component's profile (edges, card widths).</param>
    /// <returns>
    /// The top-left of every cluster box, the horizontal/vertical mirror chosen for every cluster's
    /// content, and the canvas dimensions covering every box.
    /// </returns>
    public static (IReadOnlyList<GraphPoint> Offsets, IReadOnlyList<(bool Horizontal, bool Vertical)> Flips, double Width, double Height) Arrange(
        GraphClusterSet clustered,
        IReadOnlyList<GraphLocalLayout> locals,
        GraphLayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(clustered);
        ArgumentNullException.ThrowIfNull(locals);
        ArgumentNullException.ThrowIfNull(profile);

        var layout = new Placement(clustered, locals, profile);
        layout.Arrange();

        // Normalize to origin; the canvas spans every box's right and bottom edge.
        var minX = layout.Offsets.Min(p => p.X);
        var minY = layout.Offsets.Min(p => p.Y);
        var offsets = layout.Offsets.Select(p => new GraphPoint(p.X - minX, p.Y - minY)).ToArray();

        var canvasWidth = offsets.Select((p, at) => p.X + layout.BoxWidth(at)).Max();
        var canvasHeight = offsets.Select((p, at) => p.Y + layout.BoxHeight(at)).Max();

        return (offsets, layout.Flips, canvasWidth, canvasHeight);
    }

    /// <summary>
    /// One arrangement pass over one component: the band geometry, the mirrors, and the nudges that clear
    /// ground for the relations between clusters. Holds the mutable centres so a trial can be reverted
    /// without leaking coordinates into the caller.
    /// </summary>
    private sealed class Placement
    {
        private readonly GraphClusterSet _clustered;
        private readonly IReadOnlyList<GraphLocalLayout> _locals;
        private readonly GraphLayoutProfile _profile;
        private readonly int _count;
        private readonly double[] _boxWidth;
        private readonly double[] _boxHeight;
        private readonly double[] _contentWidth;
        private readonly double[] _contentHeight;
        private readonly double[][] _quotient;
        private readonly (int Child, int Parent)[] _crossEdges;
        private readonly (int Child, int Parent)[] _strokes;
        private readonly bool[][] _touches;
        private readonly int[] _ranks;
        private readonly int _maxRank;
        private readonly List<List<int>> _bands;

        /// <summary>The x of every box's slot, which is where its centre stands along its own band.</summary>
        private readonly double[] _slotX;

        /// <summary>The centre of every cluster box, settled by the band placement then nudged to clear ground.</summary>
        private readonly GraphPoint[] _centres;

        /// <summary>
        /// Whether this picture is narrow enough for the band trade and the slides to be judged on the crossings
        /// between the boxes at all, which costs a look at every pair of relations (<see cref="GraphSearchBudget.MaximumArrangerPricedStrokes"/>).
        /// </summary>
        private readonly bool _pricesCrossings;

        /// <summary>The local index of each node inside its cluster, parallel to the profile's nodes.</summary>
        private readonly int[] _localAt;

        public Placement(GraphClusterSet clustered, IReadOnlyList<GraphLocalLayout> locals, GraphLayoutProfile profile)
        {
            _clustered = clustered;
            _locals = locals;
            _profile = profile;
            _count = clustered.Members.Count;
            _boxWidth = new double[_count];
            _boxHeight = new double[_count];
            _contentWidth = new double[_count];
            _contentHeight = new double[_count];

            for (var at = 0; at < _count; at++)
            {
                // ClusterGap of clear space on every side of the content.
                _contentWidth[at] = locals[at].Width;
                _contentHeight[at] = locals[at].Height;
                _boxWidth[at] = locals[at].Width + (2 * GraphPlacementConstants.ClusterGap);
                _boxHeight[at] = locals[at].Height + (2 * GraphPlacementConstants.ClusterGap);
            }

            _localAt = new int[profile.NodeCount];
            for (var cluster = 0; cluster < _count; cluster++)
            {
                var members = locals[cluster].Nodes;
                for (var at = 0; at < members.Count; at++)
                {
                    _localAt[members[at]] = at;
                }
            }

            _quotient = BuildQuotient();
            _crossEdges = CollectCrossEdges();
            _strokes = CollectStrokes();
            _touches = BuildTouches();
            _pricesCrossings = _strokes.Length <= GraphSearchBudget.MaximumArrangerPricedStrokes;
            _ranks = BuildClusterRanks();
            _maxRank = _ranks.Max();
            _bands = BuildBands();
            _slotX = new double[_count];
            _centres = new GraphPoint[_count];
            Flips = new (bool, bool)[_count];
        }

        public GraphPoint[] Offsets { get; private set; } = [];

        public (bool Horizontal, bool Vertical)[] Flips { get; }

        public void Arrange()
        {
            PlaceBands();
            ChooseMirrors();
            TidyOrder();
            ClearGround();
            Offsets = ComputeOffsets();
        }

        public double BoxWidth(int cluster) => _boxWidth[cluster];

        public double BoxHeight(int cluster) => _boxHeight[cluster];

        /// <summary>One node per cluster, one weight per pair equal to the number of relations between them.</summary>
        private double[][] BuildQuotient()
        {
            var matrix = new double[_count][];
            for (var at = 0; at < _count; at++)
            {
                matrix[at] = new double[_count];
            }

            foreach (var (child, parent) in _profile.Edges)
            {
                if (child == parent)
                {
                    continue;
                }

                var from = _clustered.ClusterOf[child];
                var to = _clustered.ClusterOf[parent];
                if (from != to)
                {
                    matrix[from][to]++;
                    matrix[to][from]++;
                }
            }

            return matrix;
        }

        /// <summary>
        /// The band every cluster stands in, read straight off the quotient graph: each cross-cluster relation
        /// is turned into one edge from the child cluster to the parent cluster, and the ranked profile's own
        /// longest-path rank — the same order a single component is laid out in — gives each cluster its band.
        /// A cluster a relation names takes the shallower band, so it stands above the cluster that names it.
        /// </summary>
        private int[] BuildClusterRanks()
        {
            var pairs = new HashSet<(int, int)>();
            foreach (var (child, parent) in _profile.Edges)
            {
                if (child == parent)
                {
                    continue;
                }

                var from = _clustered.ClusterOf[child];
                var to = _clustered.ClusterOf[parent];
                if (from != to)
                {
                    pairs.Add((from, to));
                }
            }

            var edges = pairs.Select(pair => (pair.Item1, pair.Item2)).ToArray();
            var widths = Enumerable.Repeat(1d, _count).ToArray();

            return GraphLayoutProfile.Create(widths, edges).Rank.ToArray();
        }

        /// <summary>The clusters grouped by band, shallowest band first; each band's order is settled when it is placed.</summary>
        private List<List<int>> BuildBands()
        {
            var bands = new List<List<int>>();
            for (var rank = 0; rank <= _maxRank; rank++)
            {
                bands.Add([]);
            }

            for (var cluster = 0; cluster < _count; cluster++)
            {
                bands[_ranks[cluster]].Add(cluster);
            }

            return bands;
        }

        /// <summary>
        /// The cross-cluster relations, deduplicated by node pair: each is tested once, whatever parallel
        /// relations share the two cards.
        /// </summary>
        private (int Child, int Parent)[] CollectCrossEdges()
        {
            var seen = new HashSet<(int, int)>();
            var result = new List<(int, int)>();
            foreach (var (child, parent) in _profile.Edges)
            {
                if (child == parent || _clustered.ClusterOf[child] == _clustered.ClusterOf[parent])
                {
                    continue;
                }

                var pair = (Math.Min(child, parent), Math.Max(child, parent));
                if (seen.Add(pair))
                {
                    result.Add((child, parent));
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Every relation the picture draws, deduplicated by node pair and without self-references: the strokes
        /// whose crossings and whose clear ground the box slides are judged on.
        /// </summary>
        private (int Child, int Parent)[] CollectStrokes()
        {
            var seen = new HashSet<(int, int)>();
            var result = new List<(int, int)>();
            foreach (var (child, parent) in _profile.Edges)
            {
                if (child == parent)
                {
                    continue;
                }

                var pair = (Math.Min(child, parent), Math.Max(child, parent));
                if (seen.Add(pair))
                {
                    result.Add((child, parent));
                }
            }

            return result.ToArray();
        }

        /// <summary>For every cluster, which strokes have an end inside it.</summary>
        private bool[][] BuildTouches()
        {
            var touches = new bool[_count][];
            for (var cluster = 0; cluster < _count; cluster++)
            {
                touches[cluster] = new bool[_strokes.Length];
            }

            for (var at = 0; at < _strokes.Length; at++)
            {
                touches[_clustered.ClusterOf[_strokes[at].Child]][at] = true;
                touches[_clustered.ClusterOf[_strokes[at].Parent]][at] = true;
            }

            return touches;
        }

        /// <summary>
        /// Settles every box centre, band by band from the top down. Each band is packed left to right with a
        /// full cluster gap between boxes (so no two boxes ever share ground), then its order is swept down and
        /// back up a few times — each band reordered by the barycentre of the clusters it links to above and
        /// below, and slid under them — which is how the relations between the bands are made to cross as
        /// little as possible. The bands keep their rank order, so the picture stays top-down.
        /// </summary>
        private void PlaceBands()
        {
            // Seed every band in numeric order, packed from the origin; the sweeps then spread and align them.
            for (var rank = 0; rank <= _maxRank; rank++)
            {
                PackBand(_bands[rank], _slotX, 0d);
            }

            for (var pass = 0; pass < Sweeps; pass++)
            {
                for (var rank = 1; rank <= _maxRank; rank++)
                {
                    OrderAndPack(_bands[rank], _slotX);
                }

                for (var rank = _maxRank - 1; rank >= 0; rank--)
                {
                    OrderAndPack(_bands[rank], _slotX);
                }
            }

            var y = 0d;
            for (var rank = 0; rank <= _maxRank; rank++)
            {
                var band = _bands[rank];
                if (band.Count == 0)
                {
                    continue;
                }

                var bandHeight = band.Max(cluster => _boxHeight[cluster]);
                foreach (var cluster in band)
                {
                    _centres[cluster] = new GraphPoint(_slotX[cluster], y + (bandHeight / 2));
                }

                y += bandHeight + GraphPlacementConstants.ClusterGap;
            }
        }

        /// <summary>
        /// Trades neighbouring boxes within each band for the trade that draws fewer relations across one
        /// another, keeping clear ground as the tie-break — the same two terms the contest ranks layouts by,
        /// measured on the boxes themselves. A trade never moves a box out of its band, so the picture stays
        /// top-down, and a trade is kept only while it strictly improves the picture. A picture wider than the
        /// arranger's priced stroke count keeps the order the sweeps gave it: pricing every trade would compare
        /// every relation with every other one, and that is the clock a catalog needs to draw itself.
        /// </summary>
        private void TidyOrder()
        {
            if (!_pricesCrossings)
            {
                return;
            }

            for (var round = 0; round < OrderRounds; round++)
            {
                var improved = false;

                for (var rank = 0; rank <= _maxRank; rank++)
                {
                    var band = _bands[rank];
                    var current = PictureCost(int.MaxValue, int.MaxValue);

                    for (var at = 0; at + 1 < band.Count; at++)
                    {
                        var origin = _slotX[band[0]];

                        Swap(band, at, origin);
                        var trial = PictureCost(current.Crossings, current.Blocked);

                        if (trial.CompareTo(current) < 0)
                        {
                            current = trial;
                            improved = true;
                        }
                        else
                        {
                            Swap(band, at, origin);
                        }
                    }
                }

                if (!improved)
                {
                    return;
                }
            }
        }

        /// <summary>Exchanges two neighbouring boxes of a band and repacks the band from the given origin.</summary>
        private void Swap(List<int> band, int at, double origin)
        {
            (band[at], band[at + 1]) = (band[at + 1], band[at]);
            PackBand(band, _slotX, origin);

            foreach (var cluster in band)
            {
                _centres[cluster] = new GraphPoint(_slotX[cluster], _centres[cluster].Y);
            }
        }

        /// <summary>Orders a band by the barycentre of the clusters it links to, then repacks it under that anchor.</summary>
        private void OrderAndPack(List<int> band, double[] slotX)
        {
            var members = band.ToArray();
            var keys = members.Select(cluster => BarycentreX(cluster, slotX)).ToArray();
            var order = Enumerable.Range(0, members.Length)
                .OrderBy(at => keys[at] ?? double.MaxValue)
                .ThenBy(at => members[at])
                .ToList();
            band.Clear();
            band.AddRange(order.Select(at => members[at]));

            PackBand(band, slotX, Anchor(band, slotX));
        }

        /// <summary>Packs a band left to right from the given origin, keeping a full cluster gap between boxes.</summary>
        private void PackBand(IReadOnlyList<int> band, double[] slotX, double origin)
        {
            var cursor = origin;
            for (var at = 0; at < band.Count; at++)
            {
                if (at > 0)
                {
                    cursor += (_boxWidth[band[at - 1]] + _boxWidth[band[at]]) / 2 + GraphPlacementConstants.ClusterGap;
                }

                slotX[band[at]] = cursor;
            }
        }

        /// <summary>
        /// Where to start a band so its anchored clusters sit over the clusters they link to: the mean gap
        /// between where the anchored clusters now stand and where their links stand. A band with no links
        /// keeps the origin, so nothing drifts.
        /// </summary>
        private double Anchor(IReadOnlyList<int> band, double[] slotX)
        {
            var targets = new List<double>();
            var actual = new List<double>();
            for (var at = 0; at < band.Count; at++)
            {
                var target = BarycentreX(band[at], slotX);
                if (target is not null)
                {
                    targets.Add(target.Value);
                    actual.Add(slotX[band[at]]);
                }
            }

            return targets.Count == 0 ? 0d : targets.Average() - actual.Average();
        }

        /// <summary>
        /// The mean x of the clusters this one is linked to, over those in another band; null when it links to
        /// none. Both directions count, so a sweep pulls a band toward the clusters above and below it alike.
        /// </summary>
        private double? BarycentreX(int cluster, double[] slotX)
        {
            var wanted = new List<double>();
            for (var next = 0; next < _count; next++)
            {
                if (next != cluster && _quotient[cluster][next] > 0 && _ranks[next] != _ranks[cluster])
                {
                    wanted.Add(slotX[next]);
                }
            }

            return wanted.Count == 0 ? null : wanted.Average();
        }

        /// <summary>
        /// Each cluster keeps the mirror image (none, or flipped in x) with the smallest summed length of its
        /// relations to other clusters; ties keep the first option. Mirroring in x keeps every distance inside
        /// the cluster, so the cluster's own picture is unchanged. A box is never flipped in y: its content is
        /// laid out ranked, parent over child, and a vertical mirror would bury the parent at the box's foot.
        /// </summary>
        private void ChooseMirrors()
        {
            for (var cluster = 0; cluster < _count; cluster++)
            {
                var best = false;
                var bestCost = double.MaxValue;
                foreach (var horizontal in new[] { false, true })
                {
                    var cost = 0d;
                    foreach (var (child, parent) in _crossEdges)
                    {
                        var start = CardCentre(child, cluster, horizontal, false);
                        var end = CardCentre(parent, cluster, horizontal, false);
                        cost += GraphGeometry.Distance(start, end);
                    }

                    if (cost < bestCost - 1e-9)
                    {
                        bestCost = cost;
                        best = horizontal;
                    }
                }

                Flips[cluster] = (best, false);
            }
        }

        /// <summary>
        /// Slides boxes along their own band until the picture stops drawing relations over cards it does not
        /// join. A trial is judged by the crossings its box creates first and only then by how many relations run
        /// over a foreign card, so a move that buries two relations under tables to buy one crossing is refused —
        /// the same order the contest compares the two in. A move that would leave a box standing on another
        /// box's ground is refused outright. Nothing here moves a card inside a cluster and no cluster leaves its
        /// band — a box only changes column, never row — so the picture stays top-down.
        /// </summary>
        private void ClearGround()
        {
            for (var round = 0; round < GroundRounds; round++)
            {
                var improved = false;

                for (var cluster = 0; cluster < _count; cluster++)
                {
                    if (!TouchesCrossEdge(cluster))
                    {
                        continue;
                    }

                    var original = _centres[cluster];
                    var best = original;
                    var bestCost = GroundCost(cluster, int.MaxValue, int.MaxValue);

                    for (var step = 1; step <= GraphSearchBudget.MaximumClusterAngleSteps; step++)
                    {
                        foreach (var sign in new[] { 1, -1 })
                        {
                            var trial = new GraphPoint(
                                original.X + (sign * step * GraphPlacementConstants.ClusterGap), original.Y);
                            if (SharesGround(cluster, trial))
                            {
                                continue;
                            }

                            _centres[cluster] = trial;
                            var cost = GroundCost(cluster, bestCost.Crossings, bestCost.Blocked);
                            if (cost.CompareTo(bestCost) < 0)
                            {
                                bestCost = cost;
                                best = trial;
                            }
                        }
                    }

                    _centres[cluster] = best;
                    improved |= best != original;
                }

                if (!improved)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// What the whole picture costs as the boxes stand: the pairs of relations drawn across one another,
        /// then the relations drawn over a card they do not join. Both counts stop as soon as they pass the
        /// limits, because a picture already worse on the first term cannot be made good by the second.
        /// </summary>
        private (int Crossings, int Blocked) PictureCost(int crossingLimit, int blockedLimit)
        {
            var cards = BuildCards();
            var crossings = 0;

            for (var first = 0; first < _strokes.Length; first++)
            {
                for (var second = first + 1; second < _strokes.Length; second++)
                {
                    if (Crosses(_strokes[first], _strokes[second], cards))
                    {
                        crossings++;
                        if (crossings > crossingLimit)
                        {
                            return (crossings, 0);
                        }
                    }
                }
            }

            return (crossings, BlockedTotal(cards, blockedLimit));
        }

        /// <summary>
        /// What the picture costs while one box stands where it does: the crossing pairs that involve a relation
        /// leaving that box, then the relations drawn over a card they do not join. The crossing term is only
        /// counted on a picture narrow enough to price it, and both terms stop at the given limits.
        /// </summary>
        private (int Crossings, int Blocked) GroundCost(int cluster, int crossingLimit, int blockedLimit)
        {
            var cards = BuildCards();
            var crossings = CrossingsTouching(cluster, cards, crossingLimit);
            return crossings > crossingLimit
                ? (crossings, 0)
                : (crossings, BlockedTotal(cards, blockedLimit));
        }

        /// <summary>
        /// How many relation strokes are drawn over a card they do not join, as the boxes stand. Counting stops
        /// once the given limit is passed, since a slide is only kept when it buries fewer relations than it
        /// found.
        /// </summary>
        private int BlockedTotal(NodePlacement[] cards, int limit)
        {
            var grow = GraphGeometryContract.CardCenterlineClearance;
            var blocked = 0;

            foreach (var (child, parent) in _strokes)
            {
                var start = cards[child].Centre;
                var end = cards[parent].Centre;
                var left = Math.Min(start.X, end.X);
                var right = Math.Max(start.X, end.X);
                var top = Math.Min(start.Y, end.Y);
                var bottom = Math.Max(start.Y, end.Y);

                foreach (var card in cards)
                {
                    if (card.NodeIndex == child || card.NodeIndex == parent
                        || card.Right + grow < left || card.TopLeft.X - grow > right
                        || card.Bottom + grow < top || card.TopLeft.Y - grow > bottom)
                    {
                        continue;
                    }

                    if (GraphGeometry.SegmentHitsCardEnvelope(start, end, card))
                    {
                        blocked++;
                        if (blocked > limit)
                        {
                            return blocked;
                        }

                        break;
                    }
                }
            }

            return blocked;
        }

        /// <summary>
        /// The pairs of relations drawn across one another in which at least one stroke leaves this box. Only
        /// those pairs can change when the box slides — every other pair keeps the two strokes it already had —
        /// so this is the whole difference the move makes to the picture's crossings. A box of a picture too wide
        /// to price reports zero crossings, leaving the slide to judge the ground alone, and the count stops as
        /// soon as it passes the given limit.
        /// </summary>
        private int CrossingsTouching(int cluster, NodePlacement[] cards, int limit)
        {
            if (!_pricesCrossings)
            {
                return 0;
            }

            var crossings = 0;
            for (var first = 0; first < _strokes.Length; first++)
            {
                for (var second = first + 1; second < _strokes.Length; second++)
                {
                    if ((_touches[cluster][first] || _touches[cluster][second])
                        && Crosses(_strokes[first], _strokes[second], cards))
                    {
                        crossings++;
                        if (crossings > limit)
                        {
                            return crossings;
                        }
                    }
                }
            }

            return crossings;
        }

        /// <summary>Whether two relations run across one another, ignoring the pair that meets at a shared card.</summary>
        private static bool Crosses((int Child, int Parent) first, (int Child, int Parent) second, NodePlacement[] cards)
        {
            if (first.Child == second.Child || first.Child == second.Parent
                || first.Parent == second.Child || first.Parent == second.Parent)
            {
                return false;
            }

            var (a, b) = (cards[first.Child].Centre, cards[first.Parent].Centre);
            var (c, d) = (cards[second.Child].Centre, cards[second.Parent].Centre);
            if (Math.Max(a.X, b.X) < Math.Min(c.X, d.X) || Math.Max(c.X, d.X) < Math.Min(a.X, b.X)
                || Math.Max(a.Y, b.Y) < Math.Min(c.Y, d.Y) || Math.Max(c.Y, d.Y) < Math.Min(a.Y, b.Y))
            {
                return false;
            }

            return GraphGeometry.SegmentsCross(a, b, c, d);
        }

        /// <summary>Whether the cluster takes part in any relation that leaves its box.</summary>
        private bool TouchesCrossEdge(int cluster) =>
            _crossEdges.Any(edge => _clustered.ClusterOf[edge.Child] == cluster || _clustered.ClusterOf[edge.Parent] == cluster);

        /// <summary>Whether putting this box at the trial centre would leave it standing on another box's ground.</summary>
        private bool SharesGround(int cluster, GraphPoint trial)
        {
            for (var next = 0; next < _count; next++)
            {
                if (next == cluster)
                {
                    continue;
                }

                var width = Math.Min(trial.X + (_boxWidth[cluster] / 2), _centres[next].X + (_boxWidth[next] / 2))
                    - Math.Max(trial.X - (_boxWidth[cluster] / 2), _centres[next].X - (_boxWidth[next] / 2));
                var height = Math.Min(trial.Y + (_boxHeight[cluster] / 2), _centres[next].Y + (_boxHeight[next] / 2))
                    - Math.Max(trial.Y - (_boxHeight[cluster] / 2), _centres[next].Y - (_boxHeight[next] / 2));
                if (width > 0 && height > 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Every card's placement at the current centres and mirrors.</summary>
        private NodePlacement[] BuildCards()
        {
            var cards = new NodePlacement[_profile.NodeCount];
            for (var cluster = 0; cluster < _count; cluster++)
            {
                var contentTop = ContentTopLeft(cluster);
                var members = _locals[cluster].Nodes;
                for (var at = 0; at < members.Count; at++)
                {
                    var node = members[at];
                    var local = _locals[cluster].TopLefts[at];
                    var x = contentTop.X + (Flips[cluster].Horizontal
                        ? _contentWidth[cluster] - local.X - _profile.Widths[node] : local.X);
                    var y = contentTop.Y + (Flips[cluster].Vertical
                        ? _contentHeight[cluster] - local.Y - GraphCard.Height : local.Y);
                    cards[node] = new NodePlacement(node, new GraphPoint(x, y), _profile.Widths[node], GraphCard.Height);
                }
            }

            return cards;
        }

        /// <summary>The top-left of the cluster's content: the box centre lifted by half the content size.</summary>
        private GraphPoint ContentTopLeft(int cluster)
        {
            var centrePoint = BoxCentre(cluster);
            return new GraphPoint(
                centrePoint.X - (_contentWidth[cluster] / 2),
                centrePoint.Y - (_contentHeight[cluster] / 2));
        }

        /// <summary>
        /// One card's centre with one candidate mirror applied to the cluster being tested, the other
        /// clusters at their chosen mirror: what the cross-cluster lengths cost under the trial.
        /// </summary>
        private GraphPoint CardCentre(int node, int trialCluster, bool trialHorizontal, bool trialVertical)
        {
            var cluster = _clustered.ClusterOf[node];
            var at = _localAt[node];
            var horizontal = cluster == trialCluster ? trialHorizontal : Flips[cluster].Horizontal;
            var vertical = cluster == trialCluster ? trialVertical : Flips[cluster].Vertical;

            var centrePoint = BoxCentre(cluster);
            var contentLeft = centrePoint.X - (_contentWidth[cluster] / 2);
            var contentTop = centrePoint.Y - (_contentHeight[cluster] / 2);
            var x = contentLeft + Flip(
                _locals[cluster].TopLefts[at].X + (_profile.Widths[node] / 2), _contentWidth[cluster], horizontal);
            var y = contentTop + Flip(
                _locals[cluster].TopLefts[at].Y + (GraphCard.Height / 2), _contentHeight[cluster], vertical);

            return new GraphPoint(x, y);
        }

        private GraphPoint BoxCentre(int cluster) => _centres[cluster];

        private GraphPoint[] ComputeOffsets()
        {
            var offsets = new GraphPoint[_count];
            for (var cluster = 0; cluster < _count; cluster++)
            {
                var centrePoint = BoxCentre(cluster);
                offsets[cluster] = new GraphPoint(
                    centrePoint.X - (_boxWidth[cluster] / 2),
                    centrePoint.Y - (_boxHeight[cluster] / 2));
            }

            return offsets;
        }

        private static double Flip(double coordinate, double extent, bool flip) => flip ? extent - coordinate : coordinate;
    }
}
