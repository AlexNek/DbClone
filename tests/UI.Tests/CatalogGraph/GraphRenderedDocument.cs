using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbClone.UI.Services.GraphLayout;
namespace UI.Tests.CatalogGraph;
/// <summary>
/// The picture a reader gets, read back from the document the application writes: the card rectangles, the
/// painted strokes, the labels with the ink they print in, and the JSON island that states which two cards each
/// relation joins. Nothing here asks the layout engine; every term is taken from the markup, so no exemption in
/// the validator can turn a covered relation into a reported zero.
/// </summary>
internal sealed partial class GraphRenderedDocument
{
    private const int SamplesPerSegment = 25;
    private const double BorderTolerance = 1.0;
    private readonly List<NodePlacement> _cards = [];
    private readonly List<bool> _cardAway = [];
    private readonly List<(int Index, string D, bool Away, GraphPoint Shift)> _strokes = [];
    private readonly List<(int Index, GraphPoint Anchor, string Text)> _labels = [];
    private readonly Dictionary<int, (int Child, int Parent, bool Self)> _relations = [];
    private readonly Dictionary<int, string> _names = [];
    private readonly List<string> _overprints = [];
    private IReadOnlyList<(int Edge, IReadOnlyList<GraphPoint> Points, bool Chord)> _lines = [];
    private GraphRenderedDocument()
    {
    }
    /// <summary>Gets the painted cards, the ones not hidden by <c>away</c>.</summary>
    public int PaintedCards => _cardAway.Count(away => !away);
    /// <summary>Gets the painted relations, the ones not hidden by <c>away</c>.</summary>
    public int PaintedRelations => _lines.Count;
    /// <summary>Gets the relations whose stroke passes a painted card they do not join, self-references apart.</summary>
    public int HiddenRelations { get; private set; }
    /// <summary>Gets the hidden relations drawn as a straight chord: the case the measurement contract says the cards are painted over.</summary>
    public int HiddenChords { get; private set; }
    /// <summary>Gets the hidden relations drawn as a curve: the case the engine's hard rule holds out of every card it does not join.</summary>
    public int HiddenCurves { get; private set; }
    /// <summary>Gets the self-references whose loop passes a painted card.</summary>
    public int HiddenLoops { get; private set; }
    /// <summary>Gets the relation/card occlusion pairs.</summary>
    public int Occlusions { get; private set; }
    /// <summary>Gets how many different cards occlude something.</summary>
    public int DistinctOccludingCards { get; private set; }
    /// <summary>Gets the node index of the table that most painted relations meet.</summary>
    public int BusiestTable { get; private set; } = -1;
    /// <summary>Gets how many painted relations meet the busiest table.</summary>
    public int BusiestTableRelations { get; private set; }
    /// <summary>Gets the hidden strokes meeting the busiest table that are curves — the part of its fan a card must not cover.</summary>
    public int FanHiddenCurves { get; private set; }
    /// <summary>Gets the hidden relations among those that meet the busiest table.</summary>
    public int HiddenMeetingBusiestTable { get; private set; }
    /// <summary>Gets the crossings between two straight strokes.</summary>
    public int Crossings { get; private set; }
    /// <summary>Gets the crossings between any two painted strokes.</summary>
    public int AllCrossings { get; private set; }
    /// <summary>Gets the pairs of arrow ends on one border closer than the contract's port step.</summary>
    public int MergedPortMarks { get; private set; }
    /// <summary>Gets the painted relation labels whose anchor lies strictly inside a painted card.</summary>
    public int LabelsOnCards { get; private set; }
    /// <summary>Gets the pairs of labels that would print on one place if the reader revealed them together.</summary>
    public int LabelOverprints { get; private set; }
    /// <summary>Gets one evidence line per overprinting pair, naming the two constraints and where they stand.</summary>
    public IReadOnlyList<string> OverprintReport => _overprints;
    /// <summary>Gets the strokes drawn over 600 px.</summary>
    public int LongStrokes { get; private set; }
    /// <summary>Gets the distinct card rows (distinct top y) of the painted cards.</summary>
    public int Rows { get; private set; }
    /// <summary>Gets the most painted cards in one row.</summary>
    public int WidestRow { get; private set; }
    /// <summary>Gets the canvas width the cards need.</summary>
    public double CanvasWidth { get; private set; }
    /// <summary>Gets the canvas height the cards need.</summary>
    public double CanvasHeight { get; private set; }
    /// <summary>Gets the share of the canvas the painted cards cover.</summary>
    public double CardInkFraction { get; private set; }
    /// <summary>Gets the node index of every relation hidden by a card, in relation order.</summary>
    public IReadOnlyList<int> HiddenRelationIndexes { get; private set; } = [];
    /// <summary>Gets the constraint name of every hidden relation, as the island states it, in relation order.</summary>
    public IReadOnlyList<string> HiddenRelationNames => HiddenRelationIndexes.Select(index => _names.GetValueOrDefault(index, string.Empty)).ToArray();
    /// <summary>Gets whether the document marks anything <c>away</c>.</summary>
    public bool HasAway => _cardAway.Any(away => away) || _strokes.Any(stroke => stroke.Away);
    /// <summary>Reads one rendered document.</summary>
    /// <param name="html">The document the renderer wrote.</param>
    /// <returns>The reading.</returns>
    public static GraphRenderedDocument Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = new GraphRenderedDocument();
        document.ReadIsland(html);
        document.ReadCards(html);
        document.ReadStrokes(html);
        document.ReadLabels(html);
        document.Measure();
        return document;
    }
    /// <summary>Renders the terms as one line of evidence.</summary>
    /// <returns>The line.</returns>
    public string Line() =>
        string.Create(CultureInfo.InvariantCulture,
            $"cards={PaintedCards} relations={PaintedRelations} hidden={HiddenRelations} fan={HiddenMeetingBusiestTable}/{BusiestTableRelations} crossings={Crossings} all={AllCrossings} merged={MergedPortMarks} labels={LabelsOnCards} overprints={LabelOverprints} rows={Rows} widest={WidestRow} canvas={CanvasWidth:F0}x{CanvasHeight:F0} ink={CardInkFraction:P1} loops={HiddenLoops}");
    private void ReadIsland(string html)
    {
        var start = html.IndexOf("id=\"data\">", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("The document carries no data island.");
        }
        start += "id=\"data\">".Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        using var island = JsonDocument.Parse(html[start..end]);
        foreach (var edge in island.RootElement.GetProperty("edges").EnumerateArray())
        {
            _relations[edge.GetProperty("i").GetInt32()] =
                (edge.GetProperty("c").GetInt32(), edge.GetProperty("p").GetInt32(), edge.GetProperty("self").GetBoolean());
            _names[edge.GetProperty("i").GetInt32()] = edge.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty;
        }
    }
    private void ReadCards(string html)
    {
        foreach (Match node in NodePattern().Matches(html))
        {
            var rect = RectPattern().Match(node.Groups["body"].Value);
            var index = int.Parse(node.Groups["n"].Value, CultureInfo.InvariantCulture);
            while (_cards.Count <= index)
            {
                _cards.Add(new NodePlacement(_cards.Count, default, 0, 0));
                _cardAway.Add(true);
            }
            // A table the reader carried by hand keeps its written rect and gains a translate on its group: the
            // card he sees stands where the rect and that shift add up, and the arrows were reflowed to it.
            var shift = TranslatePattern().Match(node.Groups["attrs"].Value);
            _cards[index] = new NodePlacement(
                index,
                new GraphPoint(
                    Number(rect, "x") + (shift.Success ? Num(shift.Groups["dx"].Value) : 0),
                    Number(rect, "y") + (shift.Success ? Num(shift.Groups["dy"].Value) : 0)),
                Number(rect, "w"),
                Number(rect, "h"));
            _cardAway[index] = HasClass(node.Groups["class"].Value, "away") || HasClass(node.Groups["class"].Value, "off");
        }
    }
    private void ReadStrokes(string html)
    {
        foreach (Match stroke in StrokePattern().Matches(html))
        {
            var classes = stroke.Groups["class"].Value;
            if (HasClass(classes, "pick"))
            {
                continue;
            }
            // A loop that followed a card the reader carried is moved by a translate on the path, not rewritten in d.
            var shift = TranslatePattern().Match(stroke.Groups["attrs"].Value);
            _strokes.Add((
                int.Parse(stroke.Groups["e"].Value, CultureInfo.InvariantCulture),
                stroke.Groups["d"].Value,
                HasClass(classes, "away") || HasClass(classes, "off"),
                shift.Success ? new GraphPoint(Num(shift.Groups["dx"].Value), Num(shift.Groups["dy"].Value)) : default));
        }
    }
    private void ReadLabels(string html)
    {
        foreach (Match label in LabelPattern().Matches(html))
        {
            _labels.Add((
                int.Parse(label.Groups["e"].Value, CultureInfo.InvariantCulture),
                new GraphPoint(Num(label.Groups["x"].Value), Num(label.Groups["y"].Value)),
                WebUtility.HtmlDecode(label.Groups["text"].Value)));
        }
    }
    private void Measure()
    {
        var painted = _cards.Where((_, index) => !_cardAway[index]).ToArray();
        MeasureGround(painted);
        _lines = _strokes.Where(stroke => !stroke.Away)
            .Select(stroke => (stroke.Index, Points: Flatten(stroke.D, stroke.Shift), Chord: !stroke.D.Contains('C', StringComparison.Ordinal)))
            .ToArray();
        MeasureOcclusion(painted);
        MeasureCrossings();
        MeasurePorts();
        var paintedRelations = _lines.Select(line => line.Edge).ToHashSet();
        LabelsOnCards = _labels.Count(label => paintedRelations.Contains(label.Index)
            && painted.Any(card => label.Anchor.X > card.TopLeft.X && label.Anchor.X < card.Right
                && label.Anchor.Y > card.TopLeft.Y && label.Anchor.Y < card.Bottom));
        MeasureOverprints(paintedRelations);
        LongStrokes = _lines.Count(line => GraphGeometry.ArcLength(line.Points) > 600);
    }
    /// <summary>
    /// Counts the labels that would print on one place in a single reveal — the reader selects a table and every
    /// relation meeting it speaks at once, so two whose ink overlaps read as one garbled constraint. The width is
    /// the document's own <c>.elabel</c> ink, taken from the text it wrote beside the anchor.
    /// </summary>
    private void MeasureOverprints(HashSet<int> painted)
    {
        var ink = new List<(int Index, GraphLabelInk Band)>();
        foreach (var label in _labels.Where(label => painted.Contains(label.Index)))
        {
            if (!_relations.TryGetValue(label.Index, out var joined))
            {
                continue;
            }
            ink.Add((label.Index, GraphLabelInk.Around(label.Anchor, label.Text, joined.Child, joined.Parent)));
        }
        for (var first = 0; first < ink.Count; first++)
        {
            for (var second = first + 1; second < ink.Count; second++)
            {
                if (ink[first].Band.RevealedWith(ink[second].Band) && ink[first].Band.Meets(ink[second].Band))
                {
                    LabelOverprints++;
                    _overprints.Add(Overprint(ink[first], ink[second]));
                }
            }
        }
    }
    private string Overprint((int Index, GraphLabelInk Band) first, (int Index, GraphLabelInk Band) second)
    {
        var (left, right) = (_labels.Single(label => label.Index == first.Index), _labels.Single(label => label.Index == second.Index));
        var tables = $"{_names.GetValueOrDefault(first.Index, "?")} | {_names.GetValueOrDefault(second.Index, "?")}";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{tables}: '{left.Text}' at ({left.Anchor.X:F0},{left.Anchor.Y:F0}) over '{right.Text}' at ({right.Anchor.X:F0},{right.Anchor.Y:F0})");
    }
    private void MeasureGround(NodePlacement[] painted)
    {
        var all = painted;
        CanvasWidth = all.Length == 0 ? 0 : all.Max(card => card.Right);
        CanvasHeight = all.Length == 0 ? 0 : all.Max(card => card.Bottom);
        CardInkFraction = CanvasWidth * CanvasHeight == 0 ? 0 : painted.Sum(card => card.Width * card.Height) / (CanvasWidth * CanvasHeight);
        var rows = painted.GroupBy(card => Math.Round(card.TopLeft.Y, 2)).ToArray();
        Rows = rows.Length;
        WidestRow = rows.Length == 0 ? 0 : rows.Max(row => row.Count());
    }
    private void MeasureOcclusion(NodePlacement[] painted)
    {
        var hidden = new List<int>();
        var occluders = new HashSet<int>();
        var meeting = new Dictionary<int, int>();
        foreach (var line in _lines.Where(line => _relations.TryGetValue(line.Edge, out var relation) && !relation.Self))
        {
            var (child, parent, _) = _relations[line.Edge];
            meeting[child] = meeting.GetValueOrDefault(child) + 1;
            meeting[parent] = meeting.GetValueOrDefault(parent) + 1;
        }
        if (meeting.Count > 0)
        {
            BusiestTableRelations = meeting.Values.Max();
            BusiestTable = meeting.Where(pair => pair.Value == BusiestTableRelations).Min(pair => pair.Key);
        }
        foreach (var line in _lines)
        {
            var (child, parent, self) = _relations[line.Edge];
            var blockers = painted.Where(card => card.NodeIndex != child && card.NodeIndex != parent
                && Hits(line.Points, card)).Select(card => card.NodeIndex).ToArray();
            if (self)
            {
                HiddenLoops += blockers.Length > 0 ? 1 : 0;
                continue;
            }
            Occlusions += blockers.Length;
            if (blockers.Length == 0)
            {
                continue;
            }
            hidden.Add(line.Edge);
            HiddenChords += line.Chord ? 1 : 0;
            HiddenCurves += line.Chord ? 0 : 1;
            occluders.UnionWith(blockers);
            if (child == BusiestTable || parent == BusiestTable)
            {
                HiddenMeetingBusiestTable++;
                FanHiddenCurves += line.Chord ? 0 : 1;
            }
        }
        HiddenRelations = hidden.Count;
        DistinctOccludingCards = occluders.Count;
        HiddenRelationIndexes = hidden;
    }
    private void MeasureCrossings()
    {
        var chords = _lines.Where(line => line.Chord && !_relations[line.Edge].Self).ToArray();
        for (var first = 0; first < chords.Length; first++)
        {
            for (var second = first + 1; second < chords.Length; second++)
            {
                if (Crosses(chords[first].Points[0], chords[first].Points[^1], chords[second].Points[0], chords[second].Points[^1]))
                {
                    Crossings++;
                }
            }
        }
        for (var first = 0; first < _lines.Count; first++)
        {
            for (var second = first + 1; second < _lines.Count; second++)
            {
                if (Cross(_lines[first].Points, _lines[second].Points))
                {
                    AllCrossings++;
                }
            }
        }
    }
    private void MeasurePorts()
    {
        var marks = new Dictionary<(int Card, char Border), List<double>>();
        foreach (var line in _lines)
        {
            var (child, parent, _) = _relations[line.Edge];
            Mark(marks, child, line.Points[0]);
            Mark(marks, parent, line.Points[^1]);
        }
        foreach (var along in marks.Values)
        {
            along.Sort();
            for (var index = 1; index < along.Count; index++)
            {
                if (along[index] - along[index - 1] < GraphGeometryContract.MinimumPortStep)
                {
                    MergedPortMarks++;
                }
            }
        }
    }
    private void Mark(Dictionary<(int Card, char Border), List<double>> marks, int card, GraphPoint point)
    {
        if (card < 0 || card >= _cards.Count)
        {
            return;
        }
        var placement = _cards[card];
        (char Border, double Distance, double Along)[] borders =
        [
            ('T', Math.Abs(point.Y - placement.TopLeft.Y), point.X),
            ('B', Math.Abs(point.Y - placement.Bottom), point.X),
            ('L', Math.Abs(point.X - placement.TopLeft.X), point.Y),
            ('R', Math.Abs(point.X - placement.Right), point.Y),
        ];
        var nearest = borders.MinBy(border => border.Distance);
        if (nearest.Distance > BorderTolerance)
        {
            return;
        }
        if (!marks.TryGetValue((card, nearest.Border), out var list))
        {
            marks[(card, nearest.Border)] = list = [];
        }
        list.Add(nearest.Along);
    }
    private static bool Cross(IReadOnlyList<GraphPoint> first, IReadOnlyList<GraphPoint> second)
    {
        for (var a = 1; a < first.Count; a++)
        {
            for (var b = 1; b < second.Count; b++)
            {
                if (Crosses(first[a - 1], first[a], second[b - 1], second[b]))
                {
                    return true;
                }
            }
        }
        return false;
    }
    /// <summary>A proper crossing: strictly inside both strokes, with no end-share margin.</summary>
    private static bool Crosses(GraphPoint a, GraphPoint b, GraphPoint c, GraphPoint d)
    {
        var (abx, aby) = (b.X - a.X, b.Y - a.Y);
        var (cdx, cdy) = (d.X - c.X, d.Y - c.Y);
        var denominator = (abx * cdy) - (aby * cdx);
        if (Math.Abs(denominator) < 1e-9)
        {
            return false;
        }
        var (acx, acy) = (c.X - a.X, c.Y - a.Y);
        var first = ((acx * cdy) - (acy * cdx)) / denominator;
        var second = ((acx * aby) - (acy * abx)) / denominator;
        return first > 1e-9 && first < 1 - 1e-9 && second > 1e-9 && second < 1 - 1e-9;
    }
    private static bool Hits(IReadOnlyList<GraphPoint> points, NodePlacement card)
    {
        for (var index = 1; index < points.Count; index++)
        {
            if (GraphGeometry.SegmentHitsCardEnvelope(points[index - 1], points[index], card))
            {
                return true;
            }
        }
        return false;
    }
    /// <summary>Reads the stroke where the browser paints it: the written path, moved by the translate on it.</summary>
    private static IReadOnlyList<GraphPoint> Flatten(string d, GraphPoint shift)
    {
        var written = Flatten(d);
        return shift.X == 0 && shift.Y == 0
            ? written
            : written.Select(point => new GraphPoint(point.X + shift.X, point.Y + shift.Y)).ToArray();
    }
    private static IReadOnlyList<GraphPoint> Flatten(string d)
    {
        var numbers = NumberPattern().Matches(d).Select(match => Num(match.Value)).ToArray();
        var points = new List<GraphPoint> { new(numbers[0], numbers[1]) };
        if (!d.Contains('C', StringComparison.Ordinal))
        {
            for (var index = 2; index + 1 < numbers.Length; index += 2)
            {
                points.Add(new GraphPoint(numbers[index], numbers[index + 1]));
            }
            return points;
        }
        for (var index = 2; index + 5 < numbers.Length; index += 6)
        {
            var start = points[^1];
            var c1 = new GraphPoint(numbers[index], numbers[index + 1]);
            var c2 = new GraphPoint(numbers[index + 2], numbers[index + 3]);
            var end = new GraphPoint(numbers[index + 4], numbers[index + 5]);
            for (var step = 1; step <= SamplesPerSegment; step++)
            {
                points.Add(GraphGeometry.CubicAt(start, c1, c2, end, (double)step / SamplesPerSegment));
            }
        }
        return points;
    }
    private static bool HasClass(string classes, string name) => classes.Split(' ').Contains(name, StringComparer.Ordinal);
    private static double Number(Match rect, string group) => Num(rect.Groups[group].Value);
    private static double Num(string text) => double.Parse(text, CultureInfo.InvariantCulture);
    [GeneratedRegex("""<g class="(?<class>node[^"]*)" data-n="(?<n>\d+)"(?<attrs>[^>]*)>(?<body>.*?)</g>""", RegexOptions.Singleline)]
    private static partial Regex NodePattern();
    [GeneratedRegex("transform=\"translate\\((?<dx>[-\\d.]+)[ ,]+(?<dy>[-\\d.]+)\\)\"")]
    private static partial Regex TranslatePattern();
    [GeneratedRegex("""<rect x="(?<x>[-\d.]+)" y="(?<y>[-\d.]+)" width="(?<w>[-\d.]+)" height="(?<h>[-\d.]+)""")]
    private static partial Regex RectPattern();
    [GeneratedRegex("""<path class="(?<class>edge[^"]*)" data-e="(?<e>\d+)" d="(?<d>[^"]*)"(?<attrs>[^>]*)""")]
    private static partial Regex StrokePattern();
    [GeneratedRegex("""<text class="elabel[^"]*" data-e="(?<e>\d+)" x="(?<x>[-\d.]+)" y="(?<y>[-\d.]+)">(?<text>[^<]*)</text>""")]
    private static partial Regex LabelPattern();
    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();
}
