using CanonicalModel.Interchange;
using DbAdapter.Definitions;
using DbAdapter.Definitions.Interchange;
using DatabaseInterchange.Format;

using Microsoft.Extensions.Logging;

namespace DatabaseInterchange.Transform;

/// <summary>
/// Request to import a catalog into a database.
/// </summary>
public sealed record ImportRequest
{
    /// <summary>Gets or sets the catalog directory path.</summary>
    public required string CatalogDirectory { get; init; }

    /// <summary>Gets or sets the target database.</summary>
    public required DatabaseTarget Target { get; init; }

    /// <summary>Gets or sets the import options.</summary>
    public ImportOptions Options { get; init; } = new();
}

/// <summary>
/// Result of an import operation.
/// </summary>
public sealed record ImportResult
{
    /// <summary>Gets whether the import succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the number of objects applied.</summary>
    public int ObjectsApplied { get; init; }

    /// <summary>Gets the number of objects skipped.</summary>
    public int ObjectsSkipped { get; init; }

    /// <summary>Gets the total number of rows imported.</summary>
    public long RowsImported { get; init; }

    /// <summary>Gets the issues encountered during import.</summary>
    public IReadOnlyList<InterchangeIssue> Issues { get; init; } = [];

    /// <summary>Gets the validation report (if validation was performed).</summary>
    public ImportValidationReport? ValidationReport { get; init; }
}

/// <summary>
/// Import plan — stages, counts, and blockers.
/// </summary>
public sealed record ImportPlan
{
    /// <summary>Gets the stages to execute.</summary>
    public IReadOnlyList<ImportPlanStage> Stages { get; init; } = [];

    /// <summary>Gets the total object count.</summary>
    public int TotalObjects { get; init; }

    /// <summary>Gets the objects that will be refused.</summary>
    public IReadOnlyList<string> RefusedObjects { get; init; } = [];

    /// <summary>Gets the dependency cycles found.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Cycles { get; init; } = [];
}

/// <summary>
/// A stage in the import plan.
/// </summary>
public sealed record ImportPlanStage
{
    /// <summary>Gets the stage name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the object count in this stage.</summary>
    public int ObjectCount { get; init; }
}

/// <summary>
/// Plans an import operation without executing it.
/// </summary>
public sealed class ImportPlanner
{
    private readonly IDatabaseAdapter _adapter;
    private readonly ILogger<ImportPlanner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportPlanner"/> class.
    /// </summary>
    public ImportPlanner(IDatabaseAdapter adapter, ILogger<ImportPlanner> logger)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Plans an import operation.
    /// </summary>
    /// <param name="request">The import request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The import plan.</returns>
    public async Task<ImportPlan> PlanAsync(ImportRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogInformation("Planning import from {CatalogDirectory}", request.CatalogDirectory);

        await using var reader = await CatalogReader.OpenAsync(request.CatalogDirectory, ct);

        // Read the catalog to count objects
        var schemas = await reader.ReadSectionAsync<SchemaDefinition>("schemas", ct);
        var tables = await reader.ReadSectionAsync<TableDefinition>("tables", ct);
        var views = await reader.ReadSectionAsync<ViewDefinition>("views", ct);
        var routines = await reader.ReadSectionAsync<RoutineDefinition>("routines", ct);
        var sequences = await reader.ReadSectionAsync<SequenceDefinition>("sequences", ct);

        var stages = new List<ImportPlanStage>
        {
            new() { Name = "Schemas", ObjectCount = schemas.Count },
            new() { Name = "Sequences", ObjectCount = sequences.Count },
            new() { Name = "Tables", ObjectCount = tables.Count },
            new() { Name = "Views", ObjectCount = views.Count },
            new() { Name = "Routines", ObjectCount = routines.Count },
            new() { Name = "Data", ObjectCount = tables.Count },
        };

        await Task.CompletedTask;

        return new ImportPlan
        {
            Stages = stages,
            TotalObjects = schemas.Count + tables.Count + views.Count + routines.Count + sequences.Count,
        };
    }
}

/// <summary>
/// Imports a catalog into a database.
/// </summary>
public sealed class SnapshotImporter
{
    private readonly IDatabaseAdapter _adapter;
    private readonly ILogger<SnapshotImporter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotImporter"/> class.
    /// </summary>
    public SnapshotImporter(IDatabaseAdapter adapter, ILogger<SnapshotImporter> logger)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Imports a catalog into a database.
    /// </summary>
    /// <param name="request">The import request.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The import result.</returns>
    public async Task<ImportResult> ImportAsync(
        ImportRequest request,
        IProgress<InterchangeProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogInformation("Starting import from {CatalogDirectory}", request.CatalogDirectory);
        progress?.Report(new InterchangeProgress { Stage = "Starting", Percentage = 0 });

        // Open catalog reader
        await using var catalogReader = await CatalogReader.OpenAsync(request.CatalogDirectory, ct);

        // Verify catalog integrity
        progress?.Report(new InterchangeProgress { Stage = "Verifying catalog", Percentage = 5 });
        var verification = await catalogReader.VerifyAsync(ct);
        if (!verification.IsValid)
        {
            return new ImportResult
            {
                Success = false,
                Issues = verification.Issues.Select(i => new InterchangeIssue(
                    "CatalogVerificationFailed",
                    EIssueSeverity.Error,
                    i)).ToList(),
            };
        }

        // Open database writer
        await using var writer = _adapter.OpenWriter(request.Target, request.Options);

        // Ensure target is empty
        progress?.Report(new InterchangeProgress { Stage = "Checking target", Percentage = 10 });
        await writer.EnsureEmptyTargetAsync(ct);

        // Every section is read before any of them is applied. Objects carry their qualified-name
        // reference (Ref), so no separate name resolution is needed.
        progress?.Report(new InterchangeProgress { Stage = "Reading catalog", Percentage = 15 });
        var schemas = await catalogReader.ReadSectionAsync<SchemaDefinition>("schemas", ct);
        var sequences = await catalogReader.ReadSectionAsync<SequenceDefinition>("sequences", ct);
        var tables = await catalogReader.ReadSectionAsync<TableDefinition>("tables", ct);
        var routines = await catalogReader.ReadSectionAsync<RoutineDefinition>("routines", ct);
        var views = await catalogReader.ReadSectionAsync<ViewDefinition>("views", ct);

        // The native file is named after the dialect the export recorded in its manifest, so nothing here
        // names an engine to find it. A catalog whose manifest carries no source dialect has no native
        // section to read.
        var sourceDialect = catalogReader.SourceDialect;
        IReadOnlyList<NativeObjectDefinition> nativeObjects = string.IsNullOrWhiteSpace(sourceDialect)
            ? []
            : await new NativeObjectReader().ReadNativeObjectsAsync(
                request.CatalogDirectory,
                sourceDialect,
                ct);

        // The file carries several kinds and one stage does not create them all, so it is routed rather
        // than handed over whole. Which stage takes which kind is the format's table; whether this
        // adapter creates the kind at all is the adapter's own declared answer. An object refused by
        // either stops the import here, where the target is still untouched, and names everything the
        // catalog holds that nothing can create.
        var capabilities = _adapter.Capabilities ?? throw new InvalidOperationException(
            "The adapter does not declare its capabilities, so the catalog's objects cannot be placed "
            + "in a stage.");
        var nativeStages = PlaceNativeObjects(nativeObjects, capabilities, out var unplaceable);

        if (unplaceable.Count > 0)
        {
            throw new InvalidOperationException(
                $"The catalog holds {unplaceable.Count} object(s) this restore cannot create: "
                + $"{string.Join(", ", unplaceable.Select(n => $"{n.Kind} \"{n.Name}\""))}. Either no "
                + "stage of the format takes that kind from the native file or the adapter declares it "
                + "does not create it, and nothing was executed.");
        }

        // The stages run in the order SchemaStage declares, which is a dependency order: an object is
        // applied once everything it names has been applied. Routines come before views because a
        // view may call a function.
        progress?.Report(new InterchangeProgress { Stage = "Applying schemas", Percentage = 20 });
        await writer.ApplySchemaAsync(SchemaStage.Schemas, AsObjects(schemas), ct);

        // Types come before the tables because a table column, a function signature and a domain can all
        // name one, so nothing that follows can be created before them.
        await ApplyNativeStagesAsync(
            writer,
            nativeStages,
            stage => stage < SchemaStage.Tables,
            percentage: 25,
            progress,
            ct);

        progress?.Report(new InterchangeProgress { Stage = "Applying sequences", Percentage = 30 });
        await writer.ApplySchemaAsync(SchemaStage.Sequences, AsObjects(sequences), ct);

        progress?.Report(new InterchangeProgress { Stage = "Applying tables", Percentage = 40 });
        await writer.ApplySchemaAsync(SchemaStage.Tables, AsObjects(tables), ct);

        progress?.Report(new InterchangeProgress { Stage = "Applying routines", Percentage = 50 });
        await writer.ApplySchemaAsync(SchemaStage.Routines, AsObjects(routines), ct);

        progress?.Report(new InterchangeProgress { Stage = "Applying views", Percentage = 60 });
        await writer.ApplySchemaAsync(SchemaStage.Views, AsObjects(views), ct);

        // The tables are handed to this stage a second time, because the format keeps a foreign key
        // inside the table that owns it and the key cannot be created until the table it points at
        // exists — which, for the whole catalog, is not true until every table is.
        progress?.Report(new InterchangeProgress { Stage = "Applying constraints", Percentage = 65 });
        await writer.ApplySchemaAsync(SchemaStage.Constraints, AsObjects(tables), ct);

        // The tables are handed to the index stage as well, for the same reason they are handed to the
        // constraint stage: which recorded indexes still have to be created is decided against the
        // constraints inside the same table. A trigger carries its own statement, in which the engine has
        // already written the function it calls, so trigger objects are flattened out of the tables and
        // passed alone.
        progress?.Report(new InterchangeProgress { Stage = "Applying indexes", Percentage = 70 });
        await writer.ApplySchemaAsync(SchemaStage.Indexes, AsObjects(tables), ct);

        progress?.Report(new InterchangeProgress { Stage = "Applying triggers", Percentage = 75 });
        await writer.ApplySchemaAsync(
            SchemaStage.Triggers,
            AsObjects([.. tables.SelectMany(t => t.Triggers)]),
            ct);

        // The native kinds the routing places after the tables are applied here: a publication lists
        // tables, and a subscription reads a publication, so neither can be created while the tables are
        // still missing. An adapter that declares it creates neither never reaches this call, because the
        // refusal above already stopped the import.
        await ApplyNativeStagesAsync(
            writer,
            nativeStages,
            stage => stage >= SchemaStage.Tables,
            percentage: 78,
            progress,
            ct);

        // Import data
        long totalRows = 0;
        var totalTables = tables.Count;
        var tablesProcessed = 0;
        var recordedRowCounts = new Dictionary<TableRef, long>();

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogInformation("Importing data for table {Table}", table.Name);

            // A table whose schema the catalog does not name cannot be addressed, so its rows are
            // not read at all.
            var schemaName = table.SchemaRef.ObjectName();
            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new InvalidOperationException(
                    $"The catalog does not name the schema the table \"{table.Name}\" lives in, so " +
                    "its rows cannot be addressed.");
            }
            var tableRef = new TableRef(schemaName, table.Ref.ObjectName());

            // The stored row is positional text, so the columns travel with it: the writer needs them
            // to know which value answers which column, what type to read it as, and which columns the
            // server computes for itself. A Binary value stored out-of-band arrives here as a range
            // payload, already validated by the format layer — the bytes are never rebuilt as text on the
            // way to the target (design §5, §6.9).
            var rows = catalogReader.ReadRowsAsync(schemaName, table.Name, ct);
            var applied = await writer.WriteTableRowsAsync(tableRef, table.Columns, rows, ct);
            totalRows += applied;

            // The count the table's own manifest states is what the target is checked against, because
            // it was written by the capture and not by this restore. A catalog holding no data folder
            // for a table — an export run without data — recorded no count, so nothing is expected.
            var recorded = await catalogReader.ReadTableRowCountAsync(schemaName, table.Name, ct);
            if (recorded is not null)
            {
                recordedRowCounts[tableRef] = recorded.Value;
            }

            tablesProcessed++;
            var percentage = 60 + (tablesProcessed * 30 / Math.Max(totalTables, 1));
            progress?.Report(new InterchangeProgress
            {
                Stage = "Importing data",
                Percentage = percentage,
                CurrentObject = table.Name,
                ObjectsProcessed = tablesProcessed,
                TotalObjects = totalTables,
            });
        }

        // Validate if requested
        ImportValidationReport? validationReport = null;
        if (request.Options.ValidateAfterImport)
        {
            progress?.Report(new InterchangeProgress { Stage = "Validating", Percentage = 95 });

            // Only the kinds this restore applies through a stage of its own are expected, and each of
            // them has to be answered by a collection the target's live model keeps. Asking about a
            // kind the import never applies — the security section, the extension section — would
            // report a target holding nothing of it as a mismatch of the restore rather than as what
            // it is: a part of the catalog no stage writes yet.
            var expectation = new TargetExpectation
            {
                ObjectCounts = BuildObjectCounts(
                    schemas,
                    tables,
                    views,
                    routines,
                    sequences,
                    nativeObjects),
                TableRowCounts = recordedRowCounts,
            };

            validationReport = await writer.ValidateAsync(expectation, ct);
        }

        progress?.Report(new InterchangeProgress { Stage = "Complete", Percentage = 100 });

        return new ImportResult
        {
            Success = true,
            ObjectsApplied = schemas.Count + tables.Count + views.Count + routines.Count
                + sequences.Count + nativeObjects.Count,
            RowsImported = totalRows,
            ValidationReport = validationReport,
        };
    }

    /// <summary>
    /// States what the target has to hold for the restore to have applied this catalog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counts come from the sections the import read, not from what the writer reports it wrote, so
    /// the check is against the catalog and not against itself. A kind the catalog holds none of is left
    /// out rather than expected as zero: a target holding something the catalog never named is worth
    /// reporting, but asking an adapter to count a kind its live model has no collection for would fill
    /// every clean restore with a warning that nothing was checked.
    /// </para>
    /// <para>
    /// Views and routines are split by the kind the model records for each, because the target answers a
    /// materialized view and a plain view from different collections, and a procedure and a function from
    /// the same one.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<EObjectKind, int> BuildObjectCounts(
        IReadOnlyList<SchemaDefinition> schemas,
        IReadOnlyList<TableDefinition> tables,
        IReadOnlyList<ViewDefinition> views,
        IReadOnlyList<RoutineDefinition> routines,
        IReadOnlyList<SequenceDefinition> sequences,
        IReadOnlyList<NativeObjectDefinition> nativeObjects)
    {
        var counts = new Dictionary<EObjectKind, int>
        {
            [EObjectKind.Schema] = schemas.Count,
            [EObjectKind.Table] = tables.Count,
            [EObjectKind.View] = views.Count(v => !v.IsMaterialized),
            [EObjectKind.MaterializedView] = views.Count(v => v.IsMaterialized),
            [EObjectKind.Sequence] = sequences.Count,
            [EObjectKind.Function] = routines.Count(r => r.Kind != EObjectKind.Procedure),
            [EObjectKind.Procedure] = routines.Count(r => r.Kind == EObjectKind.Procedure),
        };

        foreach (var group in nativeObjects.GroupBy(n => n.Kind))
        {
            counts[group.Key] = group.Count();
        }

        return counts.Where(c => c.Value > 0).ToDictionary(c => c.Key, c => c.Value);
    }

    /// <summary>
    /// Groups the native objects by the stage that creates them and reports the ones nothing can place.
    /// </summary>
    private static IReadOnlyDictionary<SchemaStage, IReadOnlyList<NativeObjectDefinition>> PlaceNativeObjects(
        IReadOnlyList<NativeObjectDefinition> nativeObjects,
        IDatabaseCapabilities capabilities,
        out IReadOnlyList<NativeObjectDefinition> unplaceable)
    {
        var stages = new Dictionary<SchemaStage, List<NativeObjectDefinition>>();
        var refused = new List<NativeObjectDefinition>();

        foreach (var native in nativeObjects)
        {
            var stage = NativeObjectRouting.StageFor(native.Kind);
            if (stage is null || !capabilities.CanCreate(native.Kind))
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

    /// <summary>
    /// Hands each native group to its own stage, in stage order, on one side of the tables.
    /// </summary>
    /// <remarks>
    /// The tables split the stages because the native file records no dependency ids: a kind the stage
    /// numbering places before them is something the tables need, and a kind after them is something that
    /// names them. Which side a stage is on is a comparison of the numbers the enum already carries, so a
    /// kind added to the routing table needs no change here.
    /// </remarks>
    private static async Task ApplyNativeStagesAsync(
        IDatabaseWriter writer,
        IReadOnlyDictionary<SchemaStage, IReadOnlyList<NativeObjectDefinition>> nativeStages,
        Func<SchemaStage, bool> sideOfTables,
        int percentage,
        IProgress<InterchangeProgress>? progress,
        CancellationToken ct)
    {
        foreach (var stage in nativeStages.Keys.Where(sideOfTables).OrderBy(stage => (int)stage))
        {
            progress?.Report(new InterchangeProgress
            {
                Stage = $"Applying {stage.ToString().ToLowerInvariant()}",
                Percentage = percentage,
            });

            await writer.ApplySchemaAsync(stage, AsObjects(nativeStages[stage]), ct);
        }
    }

    private static IReadOnlyList<ISchemaObject> AsObjects<T>(IReadOnlyList<T> objects)
        where T : ISchemaObject =>
        [.. objects];
}
