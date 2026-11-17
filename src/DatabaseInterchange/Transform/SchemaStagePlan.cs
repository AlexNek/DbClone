using CanonicalModel.Interchange;
using DbAdapter.Definitions;
using DbAdapter.Definitions.Interchange;

namespace DatabaseInterchange.Transform;

/// <summary>
/// States the order a whole catalog is created in, once, so a restore and a script cannot disagree about it.
/// </summary>
/// <remarks>
/// <para>
/// The order is a dependency order: an object is applied once everything it names has been applied. It is
/// derived from the numbering <see cref="SchemaStage"/> already carries, with three exceptions the numbering
/// alone cannot state — the tables are handed to the constraint and index stages a second time, because a
/// foreign key and an index are decided against the constraints inside the same table; a trigger travels
/// alone, flattened out of the table that owns it; and the native objects are split on one side of the
/// tables or the other, because the native file records no dependency ids.
/// </para>
/// <para>
/// The tables are handed to those later stages whole rather than per object: which recorded index still has
/// to be created is decided against the constraints inside the same table, so a table is the unit of work.
/// </para>
/// </remarks>
internal static class SchemaStagePlan
{
    /// <summary>
    /// Builds the ordered steps of one catalog.
    /// </summary>
    /// <param name="schemas">The captured schemas.</param>
    /// <param name="extensions">
    /// The captured extensions, created once their schemas exist and before anything that names an
    /// object they provide.
    /// </param>
    /// <param name="sequences">The captured sequences.</param>
    /// <param name="tables">The captured tables.</param>
    /// <param name="routines">The captured routines.</param>
    /// <param name="views">The captured views, plain and materialized.</param>
    /// <param name="nativeStages">
    /// The native objects already grouped by the stage that creates them, as
    /// <see cref="PlaceNativeObjects"/> answers.
    /// </param>
    /// <returns>Every step, in the order it must run.</returns>
    internal static IReadOnlyList<SchemaStageStep> Build(
        IReadOnlyList<SchemaDefinition> schemas,
        IReadOnlyList<ExtensionDefinition> extensions,
        IReadOnlyList<SequenceDefinition> sequences,
        IReadOnlyList<TableDefinition> tables,
        IReadOnlyList<RoutineDefinition> routines,
        IReadOnlyList<ViewDefinition> views,
        IReadOnlyDictionary<SchemaStage, IReadOnlyList<NativeObjectDefinition>> nativeStages)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(sequences);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(routines);
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(nativeStages);

        var steps = new List<SchemaStageStep>
        {
            new() { Stage = SchemaStage.Schemas, Objects = AsObjects(schemas), Percentage = 20 },
        };

        // An extension comes before the types because an operator class, a type or a function an
        // extension provides is named by the statements that follow — an index over
        // "gin_trgm_ops" cannot be created before "pg_trgm" is installed — and after the schemas
        // because an extension is installed into one.
        steps.Add(new() { Stage = SchemaStage.Extensions, Objects = AsObjects(extensions), Percentage = 22 });

        // Types come before the tables because a table column, a function signature and a domain can all
        // name one, so nothing that follows can be created before them.
        steps.AddRange(NativeSteps(nativeStages, stage => stage < SchemaStage.Tables, percentage: 25));

        steps.Add(new() { Stage = SchemaStage.Sequences, Objects = AsObjects(sequences), Percentage = 30 });
        steps.Add(new() { Stage = SchemaStage.Tables, Objects = AsObjects(tables), Percentage = 40 });

        // Routines come before views because a view may call a function.
        steps.Add(new() { Stage = SchemaStage.Routines, Objects = AsObjects(routines), Percentage = 50 });
        steps.Add(new() { Stage = SchemaStage.Views, Objects = AsObjects(views), Percentage = 60 });
        steps.Add(new() { Stage = SchemaStage.Constraints, Objects = AsObjects(tables), Percentage = 65 });
        steps.Add(new() { Stage = SchemaStage.Indexes, Objects = AsObjects(tables), Percentage = 70 });
        steps.Add(new()
        {
            Stage = SchemaStage.Triggers,
            Objects = AsObjects([.. tables.SelectMany(t => t.Triggers)]),
            Percentage = 75,
        });

        // The native kinds the routing places after the tables: a publication lists tables, and a
        // subscription reads a publication, so neither can be created while the tables are still missing.
        steps.AddRange(NativeSteps(nativeStages, stage => stage >= SchemaStage.Tables, percentage: 78));

        return steps;
    }

    /// <summary>
    /// Groups the native objects by the stage that creates them and reports the ones nothing can place.
    /// </summary>
    /// <param name="nativeObjects">The objects the catalog's native file holds.</param>
    /// <param name="canCreate">
    /// Answers whether the engine being written to creates a kind at all. A restore asks the adapter's
    /// declared capabilities; a script asks the renderer, which refuses by throwing, so it passes a
    /// predicate that accepts every kind and lets the refusal name the object.
    /// </param>
    /// <param name="unplaceable">
    /// The objects either the routing table or <paramref name="canCreate"/> refuses — everything the
    /// catalog holds that nothing can create.
    /// </param>
    /// <returns>The placed objects, keyed by stage.</returns>
    internal static IReadOnlyDictionary<SchemaStage, IReadOnlyList<NativeObjectDefinition>> PlaceNativeObjects(
        IReadOnlyList<NativeObjectDefinition> nativeObjects,
        Func<EObjectKind, bool> canCreate,
        out IReadOnlyList<NativeObjectDefinition> unplaceable)
    {
        ArgumentNullException.ThrowIfNull(nativeObjects);
        ArgumentNullException.ThrowIfNull(canCreate);

        var stages = new Dictionary<SchemaStage, List<NativeObjectDefinition>>();
        var refused = new List<NativeObjectDefinition>();

        foreach (var native in nativeObjects)
        {
            var stage = NativeObjectRouting.StageFor(native.Kind);
            if (stage is null || !canCreate(native.Kind))
            {
                refused.Add(native);
                continue;
            }

            if (!stages.TryGetValue(stage.Value, out var placed))
            {
                placed = [];
                stages[stage.Value] = placed;
            }

            placed.Add(native);
        }

        unplaceable = refused;

        return stages.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<NativeObjectDefinition>)kvp.Value);
    }

    /// <summary>Hands one type's objects to the model as the role interfaces expect them.</summary>
    internal static IReadOnlyList<ISchemaObject> AsObjects<T>(IReadOnlyList<T> objects)
        where T : ISchemaObject =>
        [.. objects];

    private static IEnumerable<SchemaStageStep> NativeSteps(
        IReadOnlyDictionary<SchemaStage, IReadOnlyList<NativeObjectDefinition>> nativeStages,
        Func<SchemaStage, bool> sideOfTables,
        int percentage) =>
        nativeStages
            .Where(kvp => sideOfTables(kvp.Key))
            .OrderBy(kvp => (int)kvp.Key)
            .Select(kvp => new SchemaStageStep
            {
                Stage = kvp.Key,
                Objects = AsObjects(kvp.Value),
                Percentage = percentage,
            });
}
