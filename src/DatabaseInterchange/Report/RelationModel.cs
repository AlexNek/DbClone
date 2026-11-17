using CanonicalModel.Interchange;

namespace DatabaseInterchange.Report;

/// <summary>
/// One captured foreign-key constraint, stated as a directed edge from the dependent table to the
/// table it references (§5.1).
/// </summary>
/// <remarks>
/// <para>
/// An edge is exactly one constraint: three constraints between the same pair are three edges, because
/// that is the fact the register prints. Edges come from <see cref="TableDefinition.ForeignKeys"/> only
/// — never from the <c>viewReference</c> relationships, since a view reading a table is not a row
/// depending on a row (§5.1, §5.8).
/// </para>
/// <para>
/// An edge whose parent was not captured keeps its full shape and is marked
/// <paramref name="ParentIsCaptured"/> = <see langword="false"/>: it stays in the register, counts in
/// the child's fan-out and in the group's relation total, creates no card for the missing parent and
/// joins no group (§5.7). A partial store therefore never loses a relation silently and never invents a
/// table that was not captured.
/// </para>
/// </remarks>
/// <param name="ChildSchema">Schema of the table declaring the constraint.</param>
/// <param name="ChildTable">Table declaring the constraint.</param>
/// <param name="ParentSchema">Schema of the referenced table, as the constraint names it.</param>
/// <param name="ParentTable">Referenced table, as the constraint names it.</param>
/// <param name="Columns">Referencing columns, in the constraint's own order.</param>
/// <param name="ReferencedColumns">Referenced columns, in the constraint's own order.</param>
/// <param name="Constraint">Constraint name as captured.</param>
/// <param name="OnDelete">The <c>ON DELETE</c> action verbatim, printed only when it leaves the default (§5.2).</param>
/// <param name="OnUpdate">The <c>ON UPDATE</c> action verbatim.</param>
/// <param name="Required">True when every referencing column is non-nullable (§5.1).</param>
/// <param name="ParentIsCaptured">True when the catalog holds the referenced table.</param>
internal sealed record ForeignKeyEdge(
    string ChildSchema,
    string ChildTable,
    string ParentSchema,
    string ParentTable,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> ReferencedColumns,
    string Constraint,
    string? OnDelete,
    string? OnUpdate,
    bool Required,
    bool ParentIsCaptured)
{
    /// <summary>Gets the qualified name of the dependent table.</summary>
    public string ChildName => ReportText.Qualified(ChildSchema, ChildTable);

    /// <summary>Gets the qualified name of the referenced table, captured or not.</summary>
    public string ParentName => ReportText.Qualified(ParentSchema, ParentTable);

    /// <summary>
    /// Gets whether the edge points at its own table. A self reference is the only place such an edge
    /// is counted (§5.8) and never forms a cyclic group (§5.5).
    /// </summary>
    public bool IsSelfReference =>
        ParentIsCaptured
        && string.Equals(ChildSchema, ParentSchema, StringComparison.Ordinal)
        && string.Equals(ChildTable, ParentTable, StringComparison.Ordinal);

    /// <summary>Gets the pair key used to find tables joined more than once (§5.4).</summary>
    public (string Left, string Right)? CapturedPair =>
        ParentIsCaptured && !IsSelfReference
            ? string.CompareOrdinal(ChildName, ParentName) <= 0
                ? (ChildName, ParentName)
                : (ParentName, ChildName)
            : null;

    /// <summary>
    /// Maps each captured schema by the reference the table section points at, which is how the writer
    /// resolved a table's schema name — the report must join on the same name, not on a second guess.
    /// </summary>
    internal static IReadOnlyDictionary<ObjectRef, string> SchemaNamesOf(InterchangeSnapshot snapshot) =>
        snapshot.Schemas
            .GroupBy(s => s.Ref)
            .ToDictionary(g => g.Key, g => g.First().Name);

    /// <summary>Gets the schema name a captured table lives under.</summary>
    internal static string SchemaOf(
        TableDefinition table,
        IReadOnlyDictionary<ObjectRef, string> schemaNames) =>
        schemaNames.TryGetValue(table.SchemaRef, out var captured)
            ? captured
            : table.SchemaRef.Value.Length > 0
                ? table.SchemaRef.ObjectName()
                : string.Empty;

    /// <summary>
    /// Resolves a table reference into the schema and table names the report prints.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the reference carries no schema, which is how an unusable or absent
    /// target is detected — such an edge is reported as pointing outside the catalog (§5.1).
    /// </returns>
    internal static bool TryResolveTable(ObjectRef reference, out string schema, out string table)
    {
        schema = string.Empty;
        table = string.Empty;

        if (reference.Value.Length == 0)
        {
            return false;
        }

        var resolvedSchema = reference.SchemaName();
        if (string.IsNullOrEmpty(resolvedSchema))
        {
            return false;
        }

        schema = resolvedSchema;
        table = reference.ObjectName();
        return table.Length > 0;
    }

    /// <summary>
    /// Renders any reference as the qualified name the report uses, falling back to whatever the
    /// reference can state. Used for the <c>viewReference</c> pairs of §5.8 Lineage, whose ends are
    /// views and tables rather than a table pair.
    /// </summary>
    internal static string QualifiedNameOf(ObjectRef reference)
    {
        if (reference.Value.Length == 0)
        {
            return ReportText.NotStated;
        }

        return TryResolveTable(reference, out var schema, out var table)
            ? ReportText.Qualified(schema, table)
            : reference.ObjectName();
    }
}

/// <summary>
/// One connected component of the captured foreign-key graph (§5.7): a home for the register rows and
/// the cards of the tables it holds.
/// </summary>
/// <remarks>
/// Groups exist only to break the register and the cards into sections of a browsable size; they carry
/// no semantics beyond membership. A group of one table holding only external or self edges is still a
/// group, because its register rows still need a home.
/// </remarks>
/// <param name="Number">One-based deterministic number, from the lowest member name (§5.7).</param>
/// <param name="Members">Captured member tables, schema-qualified, sorted ordinal.</param>
/// <param name="Edges">Every constraint incident to a member, external and self edges included.</param>
internal sealed record RelationGroup(
    int Number,
    IReadOnlyList<string> Members,
    IReadOnlyList<ForeignKeyEdge> Edges)
{
    /// <summary>Gets the table the heading names: the lowest member by <c>(schema, table)</c> ordinal.</summary>
    public string Root => Members.Count > 0 ? Members[0] : ReportText.None;

    /// <summary>Gets how many tables the group holds.</summary>
    public int TableCount => Members.Count;

    /// <summary>Gets the group's true relation total, printed above the register it then fulfils (§5.2).</summary>
    public int RelationCount => Edges.Count;
}

/// <summary>
/// The navigation unit of §5.3: one table and every constraint that touches it, in both directions.
/// </summary>
/// <remarks>
/// Both lists are printed in full, so a card stating <c>← Referenced by (18)</c> is followed by
/// exactly 18 lines (FR7b). Being referenced by 61 constraints is 61 readable rows where 61 edges out
/// of one node is the exact shape of a hairball.
/// </remarks>
/// <param name="Schema">Schema of the table.</param>
/// <param name="Table">Table name.</param>
/// <param name="Outgoing">Constraints this table declares, external targets included.</param>
/// <param name="Incoming">Constraints captured tables declare against it.</param>
/// <param name="CyclicGroup">
/// The §5.5 cyclic group this table belongs to, or <see langword="null"/>. A self reference alone never
/// yields one, so such a table carries no mark.
/// </param>
internal sealed record TableCard(
    string Schema,
    string Table,
    IReadOnlyList<ForeignKeyEdge> Outgoing,
    IReadOnlyList<ForeignKeyEdge> Incoming,
    int? CyclicGroup)
{
    /// <summary>Gets the schema-qualified name the card is headed with.</summary>
    public string QualifiedName => ReportText.Qualified(Schema, Table);

    /// <summary>Gets the total stated in the heading: every relation in either direction.</summary>
    public int RelationCount => Outgoing.Count + Incoming.Count;
}

/// <summary>
/// The coupling measurements of §5.4: the two rankings, the pairs joined more than once, the
/// cross-schema counts and the ratio of tables that carry a foreign key to those that carry none.
/// </summary>
/// <remarks>
/// A ranking is the one bounded form this feature keeps, and it is bounded without being a cut: the
/// statistics table prints the counts of <em>every</em> table and the cards print every relation of
/// every table, so the top-20 answers "which are the extremes" while nothing exists only there.
/// </remarks>
internal sealed record CouplingReport
{
    /// <summary>Gets the fan-in ranking: tables referenced by the most constraints first.</summary>
    public IReadOnlyList<Degree> FanIn { get; init; } = [];

    /// <summary>Gets the fan-out ranking: the glue tables declaring the most constraints first.</summary>
    public IReadOnlyList<Degree> FanOut { get; init; } = [];

    /// <summary>Gets the captured table pairs joined by two or more constraints, named once each.</summary>
    public IReadOnlyList<DuplicatePair> DuplicatePairs { get; init; } = [];

    /// <summary>Gets the cross-schema relation counts by direction.</summary>
    public IReadOnlyList<SchemaFlow> CrossSchema { get; init; } = [];

    /// <summary>Gets the total number of cross-schema relations.</summary>
    public int CrossSchemaTotal { get; init; }

    /// <summary>Gets how many captured tables declare at least one foreign key (§5.4's ratio numerator).</summary>
    public int TablesWithForeignKeys { get; init; }

    /// <summary>Gets how many captured tables declare none (§5.4's ratio denominator).</summary>
    /// <remarks>
    /// This counts tables that declare no constraint, which is a wider set than
    /// <see cref="RelationMap.TablesWithNoRelationships"/>: a lookup table referenced by 61 constraints
    /// declares none of them.
    /// </remarks>
    public int TablesWithoutForeignKeys { get; init; }

    /// <summary>One table's degree in one direction.</summary>
    /// <param name="Table">The schema-qualified table name.</param>
    /// <param name="Count">Relations counted in that direction.</param>
    internal sealed record Degree(string Table, int Count);

    /// <summary>Two captured tables joined by more than one constraint.</summary>
    /// <param name="Left">First table of the pair, ordinal lower.</param>
    /// <param name="Right">Second table of the pair.</param>
    /// <param name="Count">Constraints joining them.</param>
    /// <param name="Constraints">Their names, in the register's own order.</param>
    internal sealed record DuplicatePair(
        string Left,
        string Right,
        int Count,
        IReadOnlyList<string> Constraints);

    /// <summary>Relations crossing from one schema to another.</summary>
    /// <param name="From">Schema of the dependent table.</param>
    /// <param name="To">Schema of the referenced table.</param>
    /// <param name="Count">Constraints in that direction.</param>
    internal sealed record SchemaFlow(string From, string To, int Count);
}

/// <summary>
/// The relation facts after analysis (§5): every constraint, the groups, the per-table neighbourhoods,
/// the rankings and the cyclic components.
/// </summary>
/// <remarks>
/// Recomputed on every store and never persisted in this form (D2): no reader, no version field, no
/// consumer outside the renderer, so a future format change may rewrite it without breaking a caller.
/// </remarks>
internal sealed record RelationMap
{
    /// <summary>Gets every captured constraint as one edge, ordered child → parent → constraint (§5.2).</summary>
    public IReadOnlyList<ForeignKeyEdge> Edges { get; init; } = [];

    /// <summary>Gets the connected components that hold the register and the cards (§5.7).</summary>
    public IReadOnlyList<RelationGroup> Groups { get; init; } = [];

    /// <summary>Gets one card per table taking part in at least one relation, by relation count (§5.3).</summary>
    public IReadOnlyList<TableCard> Cards { get; init; } = [];

    /// <summary>Gets the coupling measurements (§5.4).</summary>
    public CouplingReport Coupling { get; init; } = new();

    /// <summary>Gets the strongly-connected components of two or more tables (§5.5).</summary>
    public IReadOnlyList<CyclicGroup> CyclicGroups { get; init; } = [];

    /// <summary>Gets the self-referencing constraints — the only place one is counted (§5.8).</summary>
    public IReadOnlyList<ForeignKeyEdge> SelfReferences { get; init; } = [];

    /// <summary>Gets the constraints pointing at a table this catalog does not hold (§5.8).</summary>
    public IReadOnlyList<ForeignKeyEdge> ExternalReferences { get; init; } = [];

    /// <summary>
    /// Gets the tables taking part in no foreign-key relationship in either direction (§5.8). The
    /// heading words this as what was checked: such a table may still be read by views, routines or
    /// application code, so the report never calls it unrelated.
    /// </summary>
    public IReadOnlyList<string> TablesWithNoRelationships { get; init; } = [];

    /// <summary>Gets the <c>viewReference</c> edges, kept out of the register and printed as lineage (§5.8).</summary>
    public IReadOnlyList<LineagePair> Lineage { get; init; } = [];

    /// <summary>Gets the number of captured constraints.</summary>
    public int ForeignKeyCount => Edges.Count;

    /// <summary>Gets how many tables take part in at least one relation.</summary>
    public int RelatedTableCount => Cards.Count;

    /// <summary>Gets how many tables are involved in a cyclic group, counted once each (§5.5).</summary>
    public int TablesInCycles => CyclicGroups.Sum(g => g.Members.Count);

    /// <summary>The §5.5 finding: one strongly-connected component of two or more tables.</summary>
    /// <param name="Number">One-based number, ordered by the component's sorted member list.</param>
    /// <param name="Members">Member tables, schema-qualified, sorted ordinal.</param>
    /// <param name="Constraints">The names of the constraints inside the component, in register order.</param>
    /// <param name="PathTables">
    /// One representative path through the component, closed back on its start — an example, never the
    /// cycle, because a component of six tables usually contains many.
    /// </param>
    /// <param name="PathConstraintCount">How many constraints that path uses out of those inside.</param>
    internal sealed record CyclicGroup(
        int Number,
        IReadOnlyList<string> Members,
        IReadOnlyList<string> Constraints,
        IReadOnlyList<string> PathTables,
        int PathConstraintCount)
    {
        /// <summary>Gets the number of constraints inside the component.</summary>
        public int InternalConstraintCount => Constraints.Count;
    }

    /// <summary>One captured <c>viewReference</c> edge, printed as <c>view → relation</c> (§5.8).</summary>
    /// <param name="Dependent">The view reading through the catalog.</param>
    /// <param name="Target">The object it reads.</param>
    internal sealed record LineagePair(string Dependent, string Target);
}
