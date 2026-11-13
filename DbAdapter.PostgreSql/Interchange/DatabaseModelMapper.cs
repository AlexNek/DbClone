using CanonicalModel.Enums;
using CanonicalModel.Interchange;

using DbAdapter.Definitions.Interchange;
using DbAdapter.PostgreSql.Execution;

using Models = CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Interchange;

/// <summary>
/// Converts the live operational model (<c>CanonicalModel.Models</c>) into the catalog
/// preservation model (<c>CanonicalModel.Interchange</c>).
/// <para>
/// The two namespaces describe the same database for different jobs and their type shapes
/// differ, so this is the only place allowed to translate between them. Portable kinds (schemas,
/// tables, columns, keys, indexes, constraints, row data) become structured interchange objects,
/// and an object whose definition text the server reported also carries it in
/// <see cref="NativeDefinition"/> — with <c>Complete</c> saying whether that text is a whole
/// statement or a fragment, because <c>pg_get_viewdef</c> returns a body and
/// <c>pg_get_functiondef</c> returns a statement.
/// </para>
/// <para>
/// Kinds with no structured home in <see cref="SnapshotSchema"/> (enum, composite and domain
/// types, publications, subscriptions) are not dropped: <see cref="MapNativeObjects"/> returns
/// them for the catalog's <c>native/&lt;dialect&gt;.json</c> section. Whether preserved text may
/// be applied is decided at import against the target dialect, never here — at export the restore
/// target is unknown.
/// </para>
/// </summary>
internal sealed class DatabaseModelMapper
{
    /// <summary>
    /// Kinds that have no representation in <see cref="SnapshotSchema"/> and therefore travel in
    /// the native section only. Every other kind is mapped structurally and carries its own native
    /// text, so listing it here too would give the importer two sources of truth for one object.
    /// </summary>
    private static readonly IReadOnlySet<EObjectKind> NativeSectionOnlyKinds =
        new HashSet<EObjectKind>
        {
            EObjectKind.Enum,
            EObjectKind.CompositeType,
            EObjectKind.Domain,
            EObjectKind.Publication,
            EObjectKind.Subscription,
        };

    private readonly string _dialect;

    /// <summary>Initializes a new instance of the <see cref="DatabaseModelMapper"/> class.</summary>
    /// <param name="dialect">The engine's dialect token, as recorded in <c>manifest.SourceDialect</c>.</param>
    public DatabaseModelMapper(string dialect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dialect);
        _dialect = dialect;
    }

    /// <summary>
    /// Maps the schema half of a snapshot: every object kind, with qualified-name references
    /// built first so child objects can reference their parents.
    /// </summary>
    public SnapshotSchema Map(Models.DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var enumTypeNames = BuildEnumTypeNameSet(model);

        return new SnapshotSchema
        {
            Database = new DatabaseInfo
            {
                Name = model.DatabaseName,
                ServerVersion = model.ServerVersion,
            },
            Schemas = MapSchemas(model),
            Tables = MapTables(model, enumTypeNames),
            Views = MapViews(model),
            Routines = MapRoutines(model),
            Sequences = MapSequences(model),
            Extensions = MapExtensions(model),
            Relationships = MapRelationships(model),
            Security = MapSecurity(model),
        };
    }

    /// <summary>
    /// Maps the objects that cannot be expressed in the neutral model and must travel as verbatim
    /// native SQL.
    /// </summary>
    public IReadOnlyList<NativeObjectDefinition> MapNativeObjects(Models.DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var extracted = new NativeObjectExtractor(_dialect).Extract(model);

        // Deterministic order: the catalog is diffable, so one database state is one byte stream.
        return [
            .. extracted
                .Where(o => NativeSectionOnlyKinds.Contains(o.Kind))
                .OrderBy(o => o.Kind.ToString(), StringComparer.Ordinal)
                .ThenBy(o => o.SchemaName, StringComparer.Ordinal)
                .ThenBy(o => o.Name, StringComparer.Ordinal)
                .Select(o => o with { Ref = NativeRef(o) }),
        ];
    }

    /// <summary>
    /// Gives a native-section object the qualified-name reference its kind answers to.
    /// </summary>
    /// <remarks>
    /// A publication and a subscription are named by the database rather than by a schema, so they take
    /// their own tokens; the rest are the three type kinds, whose single token
    /// <see cref="ObjectRef.Type"/> explains.
    /// </remarks>
    private static ObjectRef NativeRef(NativeObjectDefinition native) =>
        native.Kind switch
        {
            EObjectKind.Publication => ObjectRef.Publication(native.Name),
            EObjectKind.Subscription => ObjectRef.Subscription(native.Name),
            _ => ObjectRef.Type(native.SchemaName, native.Name),
        };

    /// <summary>
    /// Builds the signature used as a routine's identity component, so the mapper
    /// cannot drift apart on the same function.
    /// </summary>
    internal static string RoutineSignature(Models.FunctionDefinition function) =>
        string.Join(",", function.Parameters.Select(p => p.DataType.ToLowerInvariant()));

    private static IReadOnlySet<string> BuildEnumTypeNameSet(Models.DatabaseModel model)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var enumType in model.Enums)
        {
            names.Add(enumType.Name);
            names.Add($"{enumType.SchemaName}.{enumType.Name}");
        }

        return names;
    }

    private static IReadOnlyList<SchemaDefinition> MapSchemas(
        Models.DatabaseModel model) =>
        [
            // A system schema is a presence marker the operational model keeps so copy and compare
            // can compare it. The catalog holds what a restore creates, and no target lets one be
            // created: the server rejects the name itself (42939), because every database it
            // bootstrapped already holds that schema.
            .. model.Schemas
                .Where(s => !s.IsSystem)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new SchemaDefinition
                {
                    Ref = ObjectRef.Schema(s.Name),
                    Name = s.Name,
                    Owner = s.Owner,
                    SourceObjectId = s.Name,
                }),
        ];

    private IReadOnlyList<TableDefinition> MapTables(
        Models.DatabaseModel model,
        IReadOnlySet<string> enumTypeNames)
    {
        // Triggers are reported flat by schema and table, so they are grouped here to hang each
        // one under the table that owns it.
        var triggersByTable = model.Triggers
            .GroupBy(t => $"{t.SchemaName}\u0000{t.TableName}")
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // The metadata reader reports a partition's parent as one "schema.table" key, so parents are
        // resolved by matching that key whole instead of splitting it: a quoted schema name can
        // itself contain a dot, and the catalog built the key from the parent's own two names, so an
        // equal key is the same table.
        var parentRefs = model.Tables.ToLookup(
            t => $"{t.SchemaName}.{t.Name}",
            t => ObjectRef.Table(t.SchemaName, t.Name),
            StringComparer.Ordinal);

        return [
            .. model.Tables
                .OrderBy(t => t.SchemaName, StringComparer.Ordinal)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => MapTable(
                    t,
                    enumTypeNames,
                    triggersByTable,
                    parentRefs)),
        ];
    }

    private TableDefinition MapTable(
        Models.TableDefinition table,
        IReadOnlySet<string> enumTypeNames,
        Dictionary<string, List<Models.TriggerDefinition>> triggersByTable,
        ILookup<string, ObjectRef> parentRefs)
    {
        var tableRef = ObjectRef.Table(table.SchemaName, table.Name);
        var parentRef = ResolveParentRef(table.ParentTable, parentRefs);
        var columns = table.Columns
            .OrderBy(c => c.OrdinalPosition)
            .Select(c => MapColumn(table, c, tableRef, enumTypeNames))
            .ToList();

        var primaryKey = table.Indexes.FirstOrDefault(i => i.IsPrimary);

        // A table with no key of any kind cannot be re-exported byte for byte, so it declares
        // source order instead of promising an order it does not have.
        var hasKey = primaryKey is not null
                     || table.UniqueConstraints.Count > 0
                     || table.Indexes.Any(i => i.IsUnique);

        triggersByTable.TryGetValue($"{table.SchemaName}\u0000{table.Name}", out var triggers);

        return new TableDefinition
        {
            Ref = tableRef,
            Name = table.Name,
            SchemaRef = ObjectRef.Schema(table.SchemaName),
            SourceObjectId = $"{table.SchemaName}.{table.Name}",
            TableKind = table.IsPartitioned ? ETableKind.Partitioned : ETableKind.Table,
            ParentRef = parentRef,
            PartitionStrategy = table.PartitionStrategy,
            PartitionBound = table.PartitionBound,
            Columns = columns,
            PrimaryKey = MapPrimaryKey(table, primaryKey, tableRef),
            ForeignKeys = [
                .. table.ForeignKeys
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .Select(f => MapForeignKey(table, f, tableRef)),
            ],
            UniqueConstraints = [
                .. table.UniqueConstraints
                    .OrderBy(u => u.Name, StringComparer.Ordinal)
                    .Select(u => MapUniqueConstraint(table, u, tableRef)),
            ],
            CheckConstraints = [
                .. table.CheckConstraints
                    .OrderBy(c => c.Name, StringComparer.Ordinal)
                    .Select(c => new CheckConstraintDefinition
                    {
                        Ref = ObjectRef.SubObject("checkConstraint", table.SchemaName, table.Name, c.Name),
                        Name = c.Name,
                        TableRef = tableRef,
                        Expression = new DatabaseExpression(_dialect, c.Expression),
                    }),
            ],
            Indexes = [
                .. table.Indexes
                    .OrderBy(i => i.Name, StringComparer.Ordinal)
                    .Select(i => new IndexDefinition
                    {
                        Ref = ObjectRef.SubObject("index", table.SchemaName, table.Name, i.Name),
                        Name = i.Name,
                        TableRef = tableRef,
                        IsUnique = i.IsUnique,
                        // The key columns the metadata reader already selected. They are stored as data so
                        // that a reader never has to read SQL to name a column; an index the engine stated
                        // no columns for is stored as no list, not as an empty one.
                        Columns = i.Columns.Count > 0 ? i.Columns : null,
                        NativeDefinition = Native(
                            i.Definition,
                            "pg_get_indexdef",
                            complete: true),
                    }),
            ],
            Triggers = triggers is null
                           ? []
                           : MapTriggers(triggers, table, tableRef),
            RowOrder = hasKey ? ERowOrder.PrimaryKey : ERowOrder.SourceOrder,

            // A child whose parent was not captured keeps no link: naming a ref for an object the
            // catalog does not hold would hand the restore a statement that cannot run.
            Notes = table.ParentTable is { Length: > 0 } && parentRef is null
                        ?
                        [
                            new InterchangeIssue(
                                "PartitionParentNotCaptured",
                                EIssueSeverity.Warning,
                                $"Table {table.SchemaName}.{table.Name} is a partition of "
                                + $"{table.ParentTable}, which is not part of the captured set, so "
                                + "the link between them is not recorded.",
                                tableRef),
                        ]
                        : [],
        };
    }

    /// <summary>
    /// Resolves the "schema.table" key a child table reports for its parent into that parent's
    /// catalog reference. Nothing is named when the key matches no captured table, or more than one.
    /// </summary>
    private static ObjectRef? ResolveParentRef(
        string? parentKey,
        ILookup<string, ObjectRef> parentRefs)
    {
        if (string.IsNullOrEmpty(parentKey))
        {
            return null;
        }

        var matches = parentRefs[parentKey].ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private ColumnDefinition MapColumn(
        Models.TableDefinition table,
        Models.ColumnDefinition column,
        ObjectRef tableRef,
        IReadOnlySet<string> enumTypeNames)
    {
        var logical = PgLogicalTypeMap.Classify(column.DataType, enumTypeNames);
        var (length, precision, scale) = PgLogicalTypeMap.ReadModifiers(column.DataType);

        return new ColumnDefinition
        {
            Ref = ObjectRef.Column(table.SchemaName, table.Name, column.Name),
            Name = column.Name,
            TableRef = tableRef,
            Ordinal = column.OrdinalPosition,
            NativeType = new NativeType(column.DataType),
            LogicalType = logical,
            Nullable = column.IsNullable,
            Length = length,
            Precision = precision,
            Scale = scale,
            DefaultValue = column.DefaultValue is null
                               ? null
                               : new DatabaseExpression(_dialect, column.DefaultValue),

            // The metadata reader reports whether a column is an identity but not whether it is
            // ALWAYS or BY DEFAULT, so the flag records what is known instead of guessing a mode.
            Identity = column.IsIdentity ? "yes" : null,
            Generated = column.IsGenerated && column.GenerationExpression is not null
                            ? new DatabaseExpression(_dialect, column.GenerationExpression)
                            : null,
            Description = column.Comment,
        };
    }

    private PrimaryKeyDefinition? MapPrimaryKey(
        Models.TableDefinition table,
        Models.IndexDefinition? primaryKey,
        ObjectRef tableRef)
    {
        if (primaryKey is null)
        {
            return null;
        }

        return new PrimaryKeyDefinition
        {
            Ref = ObjectRef.SubObject("primaryKey", table.SchemaName, table.Name, primaryKey.Name),
            Name = primaryKey.Name,
            TableRef = tableRef,
            Columns = [.. primaryKey.Columns],
            // A primary key is enforced by an index, and pg_get_indexdef reports that index as a
            // complete statement.
            NativeDefinition = Native(primaryKey.Definition, "pg_get_indexdef", complete: true),
        };
    }

    private UniqueConstraintDefinition MapUniqueConstraint(
        Models.TableDefinition table,
        Models.UniqueConstraintDefinition constraint,
        ObjectRef tableRef) =>
        new()
        {
            Ref = ObjectRef.SubObject("uniqueConstraint", table.SchemaName, table.Name, constraint.Name),
            Name = constraint.Name,
            TableRef = tableRef,
            Columns = [.. constraint.Columns],
        };

    private ForeignKeyDefinition MapForeignKey(
        Models.TableDefinition table,
        Models.ForeignKeyDefinition foreignKey,
        ObjectRef tableRef) =>
        new()
        {
            Ref = ObjectRef.SubObject("foreignKey", table.SchemaName, table.Name, foreignKey.Name),
            Name = foreignKey.Name,
            TableRef = tableRef,
            Columns = [.. foreignKey.Columns],
            ReferencedTableRef = ObjectRef.Table(
                foreignKey.ReferencedSchema,
                foreignKey.ReferencedTable),
            ReferencedColumns = [.. foreignKey.ReferencedColumns],
            OnDelete = foreignKey.DeleteRule,
            OnUpdate = foreignKey.UpdateRule,
            IsDeferrable = foreignKey.IsDeferrable,
            IsInitiallyDeferred = foreignKey.IsInitiallyDeferred,
        };

    private IReadOnlyList<TriggerDefinition> MapTriggers(
        List<Models.TriggerDefinition> triggers,
        Models.TableDefinition table,
        ObjectRef tableRef) =>
        [
            .. triggers
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => new TriggerDefinition
                {
                    Ref = ObjectRef.SubObject("trigger", table.SchemaName, table.Name, t.Name),
                    Name = t.Name,
                    TableRef = tableRef,
                    Timing = ParseTiming(t.Timing),
                    Events = ParseEvents(t.Events),
                    Orientation = t.IsRowLevel
                                     ? ETriggerOrientation.Row
                                     : ETriggerOrientation.Statement,
                    // The engine's own flag when the read reported one. The boolean is the two-state
                    // reading of it, kept as the fallback for a live model that was built without the
                    // finer value — and a state the restore cannot express then reaches it as itself and
                    // is refused there, rather than being quietly reported as enabled.
                    EnabledState = t.EnabledState ?? (t.IsEnabled ? "O" : "D"),
                    // The metadata model names the trigger's function but not its argument types,
                    // so the reference resolves by qualified name; the routine reference carries
                    // no signature, which is the form for a single-overload name.
                    ReferencedRoutineRef = ObjectRef.Routine(t.FunctionSchema, t.FunctionName),
                    // The WHEN condition the metadata reader captured, if any. The full trigger
                    // definition text also carries it, but the structured field is what a consumer
                    // reads first.
                    When = string.IsNullOrWhiteSpace(t.Condition)
                               ? null
                               : new DatabaseExpression(_dialect, t.Condition),
                    // The engine's own statement text, and the only complete record of what the
                    // trigger is: the columns named by an UPDATE OF event, the WHEN clause and the
                    // function's arguments appear nowhere in the structured fields above.
                    NativeDefinition = Native(t.Definition, "pg_get_triggerdef", complete: true),
                }),
        ];

    private IReadOnlyList<ViewDefinition> MapViews(
        Models.DatabaseModel model)
    {
        var views = model.Views
            .OrderBy(v => v.SchemaName, StringComparer.Ordinal)
            .ThenBy(v => v.Name, StringComparer.Ordinal)
            .Select(v => MapView(v.SchemaName, v.Name, v.Definition, materialized: false));

        var materialized = model.MaterializedViews
            .OrderBy(v => v.SchemaName, StringComparer.Ordinal)
            .ThenBy(v => v.Name, StringComparer.Ordinal)
            .Select(v => MapView(v.SchemaName, v.Name, v.Definition, materialized: true));

        return [.. views, .. materialized];
    }

    private ViewDefinition MapView(
        string schemaName,
        string name,
        string definition,
        bool materialized) =>
        new()
        {
            Ref = ObjectRef.View(schemaName, name),
            Name = name,
            SchemaRef = ObjectRef.Schema(schemaName),
            SourceObjectId = $"{schemaName}.{name}",

            // pg_get_viewdef returns the query body, not the CREATE statement, so the recorded
            // text is a fragment and declares itself as one.
            Definition = new DatabaseExpression(_dialect, definition),
            NativeDefinition = Native(definition, "pg_get_viewdef", complete: false),
            IsMaterialized = materialized,
        };

    private IReadOnlyList<RoutineDefinition> MapRoutines(
        Models.DatabaseModel model) =>
        [
            .. model.Functions
                .OrderBy(f => f.SchemaName, StringComparer.Ordinal)
                .ThenBy(f => f.Name, StringComparer.Ordinal)
                .Select(MapRoutine),
        ];

    private RoutineDefinition MapRoutine(Models.FunctionDefinition function)
    {
        var signature = RoutineSignature(function);

        return new RoutineDefinition
        {
            Ref = ObjectRef.Routine(function.SchemaName, function.Name, signature),
            Name = function.Name,
            Kind = function.IsProcedure ? EObjectKind.Procedure : EObjectKind.Function,
            SchemaRef = ObjectRef.Schema(function.SchemaName),
            SourceObjectId = $"{function.SchemaName}.{function.Name}({signature})",
            ReturnType = function.ReturnType,
            Parameters = [
                .. function.Parameters.Select(p => new RoutineParameter
                {
                    Name = p.Name,
                    DataType = p.DataType,
                    Mode = p.Mode switch
                    {
                        EParameterMode.In => "IN",
                        EParameterMode.Out => "OUT",
                        EParameterMode.InOut => "INOUT",
                        EParameterMode.Variadic => "VARIADIC",
                        _ => "IN",
                    },
                    DefaultValue = p.DefaultValue is null
                                       ? null
                                       : new DatabaseExpression(_dialect, p.DefaultValue),
                }),
            ],

            // pg_get_functiondef emits the whole CREATE statement, so this text is complete.
            NativeDefinition = Native(function.Definition, "pg_get_functiondef", complete: true),
            Implementations =
            [
                new RoutineImplementation
                {
                    Dialect = _dialect,
                    Language = function.Language,
                    Body = function.Definition,
                    Origin = EOrigin.Captured,
                    Requirements = new Requirements { RequiredLanguage = function.Language },
                },
            ],
        };
    }

    private IReadOnlyList<SequenceDefinition> MapSequences(
        Models.DatabaseModel model) =>
        [
            .. model.Sequences
                .OrderBy(s => s.SchemaName, StringComparer.Ordinal)
                .ThenBy(s => s.Name, StringComparer.Ordinal)
                .Select(s =>
                {
                    var seqRef = ObjectRef.Sequence(s.SchemaName, s.Name);
                    return new SequenceDefinition
                    {
                        Ref = seqRef,
                        Name = s.Name,
                        SchemaRef = ObjectRef.Schema(s.SchemaName),
                        SourceObjectId = $"{s.SchemaName}.{s.Name}",
                        DataType = s.DataType ?? "bigint",
                        StartValue = s.StartValue,
                        Increment = s.IncrementBy,
                        MinValue = s.MinValue,
                        MaxValue = s.MaxValue,
                        Cache = s.CacheSize,
                        Cycle = s.IsCycled,
                        Notes = SequenceNotes(s, seqRef),
                    };
                }),
        ];

    /// <summary>
    /// Records what a captured sequence declaration cannot say about the sequence it came from.
    /// </summary>
    private static IReadOnlyList<InterchangeIssue> SequenceNotes(
        Models.SequenceDefinition sequence,
        ObjectRef seqRef)
    {
        var notes = new List<InterchangeIssue>
        {
            // The current value is live server state the metadata model does not carry. Recording the
            // omission names a loss; inventing a value hides one.
            new(
                "SequenceStateNotCaptured",
                EIssueSeverity.Info,
                $"Sequence {sequence.SchemaName}.{sequence.Name} was captured with its "
                + "declaration only; its current value is not part of the metadata model.",
                seqRef),
        };

        if (sequence.IsOwned)
        {
            // The format has no field for the owning column, so a restore recreates this sequence
            // standalone — it exists, but no column draws from it and nothing marks it as the
            // sequence behind an identity. Naming the loss keeps the catalog honest about that.
            notes.Add(new InterchangeIssue(
                "SequenceOwnershipNotCaptured",
                EIssueSeverity.Warning,
                $"Sequence {sequence.SchemaName}.{sequence.Name} is owned by "
                + $"{sequence.OwnerTable}.{sequence.OwnerColumn}, which the interchange format "
                + "records no field for, so a restore cannot recreate the link.",
                seqRef));
        }

        return notes;
    }

    private IReadOnlyList<ExtensionDefinition> MapExtensions(
        Models.DatabaseModel model) =>
        [
            .. model.Extensions
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .Select(e => new ExtensionDefinition
                {
                    Ref = ObjectRef.Extension(e.Name),
                    Name = e.Name,
                    Version = e.Version,
                    Schema = e.SchemaName,
                    SourceObjectId = e.Name,

                    // Reconstructed from the catalog row, not read from the server: a fragment of
                    // what the source's own CREATE statement said.
                    NativeDefinition = Native(
                        $"CREATE EXTENSION IF NOT EXISTS {e.Name}",
                        "synthesized",
                        complete: false),
                }),
        ];

    private static IReadOnlyList<Relationship> MapRelationships(
        Models.DatabaseModel model)
    {
        var relationships = new List<Relationship>();

        foreach (var table in model.Tables)
        {
            var tableRef = ObjectRef.Table(table.SchemaName, table.Name);
            foreach (var foreignKey in table.ForeignKeys)
            {
                relationships.Add(new Relationship
                {
                    DependentRef = tableRef,
                    DependencyRef = ObjectRef.Table(
                        foreignKey.ReferencedSchema,
                        foreignKey.ReferencedTable),
                    Type = "foreignKey",
                });
            }
        }

        // A view depends on the relations it reads. The metadata reader reports those already
        // qualified as "schema.name", so they resolve by exact lookup against the captured
        // objects instead of being re-parsed into pieces.
        var relationsLookup = BuildRelationsLookup(model);

        foreach (var view in model.Views)
        {
            AddViewReferences(
                relationships,
                relationsLookup,
                view.SchemaName,
                view.Name,
                view.ReferencedRelations);
        }

        foreach (var materialized in model.MaterializedViews)
        {
            AddViewReferences(
                relationships,
                relationsLookup,
                materialized.SchemaName,
                materialized.Name,
                materialized.ReferencedRelations);
        }

        return [
            .. relationships
                .OrderBy(r => r.DependentRef.Value, StringComparer.Ordinal)
                .ThenBy(r => r.DependencyRef.Value, StringComparer.Ordinal)
                .ThenBy(r => r.Type, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Builds the lookup dictionary that resolves <c>schema.name</c> strings the metadata reader
    /// reports as referenced relations to the <see cref="ObjectRef"/> values of the objects they name.
    /// </summary>
    private static Dictionary<string, ObjectRef> BuildRelationsLookup(Models.DatabaseModel model)
    {
        var lookup = new Dictionary<string, ObjectRef>(StringComparer.Ordinal);

        // Tables first; a view of the same qualified name would not overwrite a table
        // (the old graph shared one id space keyed by qualified name, last-write-wins in
        //  sorted order; tables sort before views for the same schema.name pair).
        foreach (var table in model.Tables
                     .OrderBy(t => t.SchemaName, StringComparer.Ordinal)
                     .ThenBy(t => t.Name, StringComparer.Ordinal))
        {
            lookup[$"{table.SchemaName}.{table.Name}"] = ObjectRef.Table(table.SchemaName, table.Name);
        }

        foreach (var view in model.Views
                     .OrderBy(v => v.SchemaName, StringComparer.Ordinal)
                     .ThenBy(v => v.Name, StringComparer.Ordinal))
        {
            lookup[$"{view.SchemaName}.{view.Name}"] = ObjectRef.View(view.SchemaName, view.Name);
        }

        foreach (var mv in model.MaterializedViews
                     .OrderBy(v => v.SchemaName, StringComparer.Ordinal)
                     .ThenBy(v => v.Name, StringComparer.Ordinal))
        {
            lookup[$"{mv.SchemaName}.{mv.Name}"] = ObjectRef.View(mv.SchemaName, mv.Name);
        }

        return lookup;
    }

    private static void AddViewReferences(
        List<Relationship> relationships,
        Dictionary<string, ObjectRef> relationsLookup,
        string viewSchema,
        string viewName,
        IReadOnlyList<string>? referencedRelations)
    {
        if (referencedRelations is null || referencedRelations.Count == 0)
        {
            return;
        }

        var viewRef = ObjectRef.View(viewSchema, viewName);
        var seen = new HashSet<ObjectRef>();
        foreach (var qualifiedName in referencedRelations.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (relationsLookup.TryGetValue(qualifiedName, out var depRef) && seen.Add(depRef))
            {
                relationships.Add(new Relationship
                {
                    DependentRef = viewRef,
                    DependencyRef = depRef,
                    Type = "viewReference",
                });
            }
        }
    }

    private IReadOnlyList<PolicyDefinition> MapPolicies(
        Models.DatabaseModel model) =>
        [
            .. model.Policies
                .OrderBy(p => p.SchemaName, StringComparer.Ordinal)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new PolicyDefinition
                {
                    Ref = ObjectRef.Policy(p.SchemaName, p.TableName, p.Name),
                    Name = p.Name,
                    TableRef = ObjectRef.Table(p.SchemaName, p.TableName),
                    Command = p.Command,
                    IsPermissive = p.IsPermissive,
                    ApplicableRoles = [.. p.Roles],
                    Using = p.QualExpression is null
                                ? null
                                : new DatabaseExpression(_dialect, p.QualExpression),
                    WithCheck = p.WithCheckExpression is null
                                    ? null
                                    : new DatabaseExpression(_dialect, p.WithCheckExpression),
                }),
        ];

    private SecuritySection MapSecurity(Models.DatabaseModel model) =>
        // Roles and grants are not part of the metadata model, so those lists stay empty: an
        // absent section records what was not captured instead of implying the database had none.
        new SecuritySection { Policies = MapPolicies(model) };

    private NativeDefinition? Native(string? text, string? capturedBy, bool complete) =>
        string.IsNullOrEmpty(text)
            ? null
            : new NativeDefinition(_dialect, text, complete, capturedBy);

    private static ETriggerTiming ParseTiming(string timing) =>
        timing.Trim().ToUpperInvariant() switch
        {
            "BEFORE" => ETriggerTiming.Before,
            "AFTER" => ETriggerTiming.After,
            "INSTEAD OF" => ETriggerTiming.InsteadOf,
            _ => ETriggerTiming.Before,
        };

    private static ETriggerEvent ParseEvents(IReadOnlyList<string> events)
    {
        var result = (ETriggerEvent)0;
        foreach (var eventName in events)
        {
            result |= eventName.Trim().ToUpperInvariant() switch
            {
                "INSERT" => ETriggerEvent.Insert,
                "UPDATE" => ETriggerEvent.Update,
                "DELETE" => ETriggerEvent.Delete,
                "TRUNCATE" => ETriggerEvent.Truncate,
                _ => (ETriggerEvent)0,
            };
        }

        return result;
    }
}
