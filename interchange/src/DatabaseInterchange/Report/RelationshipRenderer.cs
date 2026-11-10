using System.Text;

namespace DatabaseInterchange.Report;

/// <summary>
/// Renders the relationship half of the report (§5) from the analysed <see cref="RelationMap"/>, and the
/// observation lines that point into it (FR16, §5.10).
/// </summary>
/// <remarks>
/// <para>
/// Pure: it turns relation facts into text and touches nothing else. Nothing in the output is a
/// diagram, a coordinate or a renderer instruction — the readability of every section is independent of
/// how many relations the schema holds, because a constraint becomes a row and a line rather than an
/// edge to untangle (§5.9).
/// </para>
/// <para>
/// Nothing is cut (FR7b): the register prints one row per captured constraint, each card prints all of
/// its lines in both directions, and only a <em>derived</em> view — a ranking, a walk — is bounded, and
/// then the walk declares itself instead of approximating (§5.6).
/// </para>
/// </remarks>
internal sealed class RelationshipRenderer
{
    private readonly RelationOutlineBuilder _outlineBuilder = new();

    /// <summary>
    /// Renders everything from the <c>## Relationships</c> heading to the end of the document.
    /// </summary>
    public string Render(RelationMap map, CatalogStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(statistics);

        var view = new View(map, statistics);
        var sections = new List<string>
        {
            RenderHeader(view),
            RenderCoupling(view),
            RenderCyclicGroups(view),
            RenderGroups(view),
            RenderCards(view),
            RenderLeftovers(view),
        };

        return string.Join("\n\n", sections.Where(s => s.Length > 0));
    }

    /// <summary>
    /// Renders the <c>## Catalog observations</c> lines (FR16): a count plus a pointer to the section
    /// that prints the items in full. Every line restates a number the detail already produces, so the
    /// section performs no analysis of its own and offers no advice.
    /// </summary>
    public IReadOnlyList<string> RenderObservations(RelationMap map, CatalogStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(statistics);

        var view = new View(map, statistics);
        var lines = new List<string>();

        AddObservation(lines, MissingPrimaryKeyLine(view));
        foreach (var line in TopFanInLines(view))
        {
            AddObservation(lines, line);
        }
        AddObservation(lines, CyclicLine(view));
        AddObservation(lines, CountedLine(view.Map.SelfReferences.Count,
            "self references", "### Self references"));
        AddObservation(lines, CountedLine(view.Map.ExternalReferences.Count,
            "foreign keys reference tables outside this catalog", "### References outside this catalog"));
        AddObservation(lines, CountedLine(view.Map.TablesWithNoRelationships.Count,
            "tables have no foreign-key relationships", "### Tables with no foreign-key relationships"));
        AddObservation(lines, view.Map.Coupling.DuplicatePairs.Count > 0
            ? $"{view.Map.Coupling.DuplicatePairs.Count} table pairs are joined by more than one constraint — " +
              "named under ### Coupling"
            : null);
        AddObservation(lines, statistics.SourceOrderTables.Count > 0
            ? $"{statistics.SourceOrderTables.Count} tables are stored in source order, so their row order is " +
              "not guaranteed to repeat — named under ## Data totals"
            : null);

        return lines.Count > 0 ? lines : [$"nothing unusual in this catalog"];
    }

    private string RenderHeader(View view)
    {
        var map = view.Map;
        var first = ReportText.Facts(
            $"{map.ForeignKeyCount} foreign keys",
            $"{map.RelatedTableCount} of {view.Statistics.TableCount} tables related",
            $"{map.Groups.Count} groups",
            $"{map.CyclicGroups.Count} cyclic groups");
        var second = ReportText.Facts(
            $"{map.SelfReferences.Count} self references",
            $"{map.ExternalReferences.Count} references outside this catalog",
            $"{map.TablesWithNoRelationships.Count} tables with no foreign-key relationships");

        return $"## Relationships\n{first}\n{second}";
    }

    private string RenderCoupling(View view)
    {
        var builder = new StringBuilder("### Coupling");

        builder.Append("\n\n").Append(RenderRanking("Fan-in — the gravity wells", view, view.Map.Coupling.FanIn));
        builder.Append("\n\n").Append(RenderRanking("Fan-out — the glue tables", view, view.Map.Coupling.FanOut));

        var duplicates = view.Map.Coupling.DuplicatePairs;
        builder.Append("\n\n").Append(duplicates.Count == 0
            ? "Pairs joined by more than one constraint: none"
            : "Pairs joined by more than one constraint:\n" +
              string.Join("\n", duplicates.Select(p =>
                  $"- {ReportText.Code(p.Left)} ↔ {ReportText.Code(p.Right)} ({p.Count})")));

        var flows = view.Map.Coupling.CrossSchema;
        var crossSchema = flows.Count == 0
            ? "Cross-schema relations: none"
            : $"Cross-schema relations: {view.Map.Coupling.CrossSchemaTotal} (" +
              string.Join(", ", flows.Select(f => $"{ReportText.Cell(f.From)} → {ReportText.Cell(f.To)} {f.Count}")) + ")";

        return $"{builder}\n{crossSchema}\n" +
               $"Tables carrying foreign keys: {view.Map.Coupling.TablesWithForeignKeys} of " +
               $"{view.Statistics.TableCount}, tables carrying none: {view.Map.Coupling.TablesWithoutForeignKeys}";
    }

    /// <summary>
    /// Prints one ranking with §6's header, stating both degrees beside the table's own row and data
    /// byte counts. The ranking is bounded; the statistics table beside it is not, which is what makes
    /// this a view rather than a cut (§5.4).
    /// </summary>
    private static string RenderRanking(
        string label,
        View view,
        IReadOnlyList<CouplingReport.Degree> ranking)
    {
        if (ranking.Count == 0)
        {
            return $"{label}: none";
        }

        var rows = ranking
            .Select(entry =>
            {
                var card = view.CardByName.TryGetValue(entry.Table, out var found) ? found : null;
                var stat = view.ByName.TryGetValue(entry.Table, out var data) ? data : null;
                return (IReadOnlyList<string>)[
                    ReportText.Cell(entry.Table),
                    ReportText.Number(card?.Incoming.Count ?? 0),
                    ReportText.Number(card?.Outgoing.Count ?? 0),
                    ReportText.Number(stat?.Rows ?? 0),
                    ReportText.Bytes(stat?.DataBytes ?? 0),
                ];
            })
            .ToList();

        return $"{label}:\n\n" +
               ReportText.Table(["table", "referenced by", "references", "rows", "data bytes"], rows);
    }

    private string RenderCyclicGroups(View view)
    {
        if (view.Map.CyclicGroups.Count == 0)
        {
            return "### Cyclic groups\nnone";
        }

        var lines = new List<string>
        {
            "### Cyclic groups",
            "One path is an example, never the cycle: a component of six tables usually holds many.",
        };

        foreach (var group in view.Map.CyclicGroups)
        {
            var members = ReportText.CodeList(group.Members);
            var complete = group.PathConstraintCount == group.InternalConstraintCount;
            var constraints = complete
                ? $": {ReportText.CodeList(group.Constraints)}"
                : string.Empty;

            var path = string.Join(" → ", group.PathTables.Select(ReportText.Code));
            var qualifier = complete ? string.Empty : $" ({group.PathConstraintCount} of the {group.InternalConstraintCount} constraints)";

            lines.Add($"{group.Number}. {group.Members.Count} tables — {members} — {group.InternalConstraintCount} constraints inside the group{constraints}");
            lines.Add($"   one path among the cycles here: {path}{qualifier}");
        }

        return string.Join("\n", lines);
    }

    private string RenderGroups(View view)
    {
        if (view.Map.Groups.Count == 0)
        {
            return string.Empty;
        }

        var inbound = view.Map.Cards.ToDictionary(c => c.QualifiedName, c => c.Incoming.Count);
        var sections = view.Map.Groups.Select(group => RenderGroup(view, group, inbound));

        return string.Join("\n\n", sections);
    }

    private string RenderGroup(View view, RelationGroup group, Dictionary<string, int> inbound)
    {
        var heading =
            $"### Group {group.Number} — {ReportText.Cell(group.Root)} " +
            $"({group.TableCount} tables, {group.RelationCount} relations)";

        var rows = group.Edges
            .Select(e => (IReadOnlyList<string>)[
                ReportText.Cell(e.ChildName),
                ReportText.ColumnList(e.Columns),
                ReportText.Cell(ParentCell(e)),
                ReportText.ColumnList(e.ReferencedColumns),
                ReportText.Cell(e.Constraint),
                ReportText.Action(e.OnDelete),
                ReportText.Action(e.OnUpdate),
                e.Required ? "yes" : "no",
            ])
            .ToList();

        var register = rows.Count == 0
            ? "none"
            : ReportText.Table(
                ["child", "columns", "→ parent", "referenced", "constraint", "on delete", "on update", "required"],
                rows);

        var outline = _outlineBuilder.Build(group, inbound);
        var outlineLines = outline.IsWalk && group.TableCount > 0
            ? "Outline — a view of the group; the register above is the complete account.\n" +
              string.Join("\n", outline.Lines)
            : string.Join("\n", outline.Lines);

        return $"{heading}\n\n{register}\n\n{outlineLines}";
    }

    /// <summary>
    /// The register's parent cell: the referenced table by name, marked when the catalog does not hold
    /// it, so a selection that excluded a table shows as a relation pointing out of the catalog instead
    /// of a relation that silently disappeared (§5.1).
    /// </summary>
    private static string ParentCell(ForeignKeyEdge edge)
    {
        if (edge.ParentIsCaptured)
        {
            return edge.ParentName;
        }

        return edge.ParentTable.Length > 0
            ? $"{edge.ParentName} (outside this catalog)"
            : "(outside this catalog)";
    }

    private string RenderCards(View view)
    {
        if (view.Map.Cards.Count == 0)
        {
            return "### Table cards — by relation count\nnone";
        }

        var sections = new List<string>(view.Map.Cards.Count + 1) { "### Table cards — by relation count" };
        sections.AddRange(view.Map.Cards.Select(card => RenderCard(view, card)));

        return string.Join("\n\n", sections);
    }

    private string RenderCard(View view, TableCard card)
    {
        var stat = view.ByName.TryGetValue(card.QualifiedName, out var found) ? found : null;
        var headingParts = new List<string>
        {
            $"{ReportText.Number(stat?.Rows ?? 0)} rows",
            ReportText.HumanSize(stat?.DataBytes ?? 0),
            $"{card.RelationCount} relations",
        };

        if (stat?.IsSourceOrder == true)
        {
            headingParts.Add("source order");
        }

        var cycleMark = card.CyclicGroup is { } number ? $" ↺ {number}" : string.Empty;
        var builder = new StringBuilder();
        builder.Append($"#### {ReportText.Code(card.QualifiedName)} — {string.Join(ReportBudget.FactSeparator, headingParts)}{cycleMark}\n");
        builder.Append(Labeled("Keys", stat?.Keys ?? ReportText.None)).Append('\n');

        // §5.3 aligns each block on its widest first field, so the arrows and the constraint names read
        // in columns. The width comes from the card's own lines, which keeps it stable across runs.
        var outgoingWidth = FirstColumnWidth(card.Outgoing, e => ReportText.ColumnList(e.Columns));
        var incomingWidth = FirstColumnWidth(
            card.Incoming,
            e => $"{ReportText.Cell(e.ChildName)}.{ReportText.ColumnList(e.Columns)}");

        builder.Append(RenderCardBlock("→ References", card.Outgoing, e => OutgoingLine(e, outgoingWidth)));
        builder.Append(RenderCardBlock("← Referenced by", card.Incoming, e => IncomingLine(e, incomingWidth)));

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// One <c>→ References</c> line: the referencing columns, the referenced table and columns, the
    /// constraint, its delete action and whether the link is required (§5.3) — the same facts the
    /// register row carries for the same constraint, never a rounded restatement of them.
    /// </summary>
    private static string OutgoingLine(ForeignKeyEdge edge, int width)
    {
        var parts = new List<string>
        {
            ReportText.ColumnList(edge.Columns).PadRight(width),
            "→",
            ReportText.Cell(ParentLabel(edge)),
            $"({ReportText.ColumnList(edge.ReferencedColumns)})",
            ReportText.Cell(edge.Constraint),
            ReportText.Action(edge.OnDelete),
        };

        if (!edge.ParentIsCaptured)
        {
            parts.Add("(outside this catalog)");
        }
        else if (edge.Required)
        {
            parts.Add("required");
        }

        return "    " + string.Join("  ", parts);
    }

    /// <summary>One <c>← Referenced by</c> line: the dependent table's column, the constraint, its action.</summary>
    private static string IncomingLine(ForeignKeyEdge edge, int width)
    {
        var parts = new List<string>
        {
            $"{ReportText.Cell(edge.ChildName)}.{ReportText.ColumnList(edge.Columns)}".PadRight(width),
            ReportText.Cell(edge.Constraint),
            ReportText.Action(edge.OnDelete),
        };

        if (edge.Required)
        {
            parts.Add("required");
        }

        return "    " + string.Join("  ", parts);
    }

    private static int FirstColumnWidth<T>(IReadOnlyList<T> items, Func<T, string> first) =>
        items.Count == 0 ? 0 : items.Max(first)!.Length;

    /// <summary>
    /// The parent as the constraint names it, or the statement that it names none — so an unusable
    /// reference is reported as unnamed instead of rendering as a bare dot (§5.1).
    /// </summary>
    private static string ParentLabel(ForeignKeyEdge edge) =>
        edge.ParentTable.Length > 0 ? edge.ParentName : "(target the constraint does not name)";

    private static string RenderCardBlock<T>(string label, IReadOnlyList<T> items, Func<T, string> render)
    {
        var builder = new StringBuilder($"{label} ({items.Count})\n");
        foreach (var item in items)
        {
            builder.Append(render(item)).Append('\n');
        }

        return builder.ToString();
    }

    private string RenderLeftovers(View view)
    {
        var map = view.Map;
        var sections = new List<string>
        {
            $"### Self references\n{LinesOrNone(map.SelfReferences.Select(SelfReferenceLine))}",

            $"### References outside this catalog\n{LinesOrNone(map.ExternalReferences.Select(RenderExternalLine))}",

            $"### Tables with no foreign-key relationships\n{NoRelationTable(view)}",

            $"### Lineage\n{LinesOrNone(map.Lineage.Select(p => $"- {ReportText.Code(p.Dependent)} → {ReportText.Code(p.Target)}"))}",
        };

        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// One self reference stated as the loop it is — column of the table pointing back into the same
    /// table (§5.8). Naming the two column lists separately is what makes it readable as a loop rather
    /// than as a relation between two identically named tables.
    /// </summary>
    private static string SelfReferenceLine(ForeignKeyEdge edge) =>
        $"- {ReportText.Cell(edge.ChildName)}.{ReportText.ColumnList(edge.Columns)} → " +
        $"{ReportText.Cell(edge.ParentName)}.{ReportText.ColumnList(edge.ReferencedColumns)}  " +
        ReportText.Cell(edge.Constraint);

    private static string RenderExternalLine(ForeignKeyEdge edge) =>
        $"- {ReportText.Cell(edge.ChildName)}.{ReportText.ColumnList(edge.Columns)} → " +
        $"{ReportText.Cell(ParentLabel(edge))}  {ReportText.Cell(edge.Constraint)}";

    private string NoRelationTable(View view)
    {
        if (view.Map.TablesWithNoRelationships.Count == 0)
        {
            return "none";
        }

        var rows = view.Map.TablesWithNoRelationships
            .Select(name =>
            {
                var stat = view.ByName.TryGetValue(name, out var found) ? found : null;
                return (IReadOnlyList<string>)[
                    ReportText.Cell(name),
                    ReportText.Number(stat?.Rows ?? 0),
                    ReportText.Bytes(stat?.DataBytes ?? 0),
                ];
            })
            .ToList();

        return ReportText.Table(["table", "rows", "data bytes"], rows);
    }

    private static string MissingPrimaryKeyLine(View view)
    {
        if (view.Statistics.TableCount == 0)
        {
            return string.Empty;
        }

        var missing = view.Statistics.Tables.Count(t => !t.HasPrimaryKey);
        return missing == 0
            ? string.Empty
            : $"{missing} tables have no primary key (of {view.Statistics.TableCount}) — " +
              "every one of them is marked in ## Statistics per table";
    }

    private static IReadOnlyList<string> TopFanInLines(View view)
    {
        var ranking = view.Map.Coupling.FanIn.Take(ReportBudget.TopFanInInObservations).ToList();
        if (ranking.Count == 0)
        {
            return [];
        }

        return
        [
            .. ranking.Select((entry, i) => i == 0
                ? $"{ReportText.Code(entry.Table)} is referenced by {entry.Count} foreign keys — the widest fan-in " +
                  "in this catalog; ranked under ### Coupling"
                : $"{ReportText.Code(entry.Table)} is referenced by {entry.Count} foreign keys; ranked under ### Coupling"),
        ];
    }

    private static string CyclicLine(View view) =>
        view.Map.CyclicGroups.Count == 0
            ? string.Empty
            : $"{view.Map.CyclicGroups.Count} cyclic groups, {view.Map.TablesInCycles} tables involved in total — " +
              "listed under ### Cyclic groups";

    private static string CountedLine(int count, string noun, string section) =>
        count == 0
            ? string.Empty
            : $"{count} {noun} — listed under {section}";

    /// <summary>
    /// Adds one observation as a bullet, skipping the facts whose count came out zero (§5.10) — a line is
    /// printed only when there is something behind it, and the section degrades to one stated line when
    /// nothing is.
    /// </summary>
    private static void AddObservation(List<string> lines, string? candidate)
    {
        if (!string.IsNullOrEmpty(candidate))
        {
            lines.Add($"- {candidate}");
        }
    }

    private static string LinesOrNone(IEnumerable<string> lines)
    {
        var list = lines.ToList();
        return list.Count == 0 ? "none" : string.Join("\n", list);
    }

    /// <summary>Renders a card's key line as a label column plus its value, §5.3's aligned layout.</summary>
    private static string Labeled(string label, string value) => label.PadRight(16) + value;

    /// <summary>
    /// One render call's inputs, joined once: the relation facts, the statistics they are quoted beside,
    /// and the table lookup §5.3's headings and §5.8's listings need.
    /// </summary>
    private sealed record View(RelationMap Map, CatalogStatistics Statistics)
    {
        public Dictionary<string, TableStat> ByName { get; } =
            Statistics.Tables.GroupBy(t => t.QualifiedName).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        public Dictionary<string, TableCard> CardByName { get; } =
            Map.Cards.GroupBy(c => c.QualifiedName).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }
}
