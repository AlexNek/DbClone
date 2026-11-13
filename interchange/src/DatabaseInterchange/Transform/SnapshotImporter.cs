using CanonicalModel.Interchange;
using DbAdapter.Definitions;
using DbAdapter.Definitions.Interchange;
using DatabaseInterchange.Format;
using DatabaseInterchange.Script;

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
    /// Plans an import from a catalog without requiring a target connection. The adapter is still
    /// retained by this planner for capability-aware planning as those decisions are added.
    /// </summary>
    public Task<ImportPlan> PlanAsync(string catalogDirectory, CancellationToken ct = default) =>
        PlanAsync(new ImportRequest
        {
            CatalogDirectory = catalogDirectory,
            Target = new DatabaseTarget(string.Empty, string.Empty),
        }, ct);


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

        // Ensure target is empty. A non-empty target is a deliberate refusal, not an import failure.
        progress?.Report(new InterchangeProgress { Stage = "Checking target", Percentage = 10 });
        try
        {
            await writer.EnsureEmptyTargetAsync(ct);
        }
        catch (InvalidOperationException ex)
        {
            return new ImportResult
            {
                Success = false,
                Issues = [new InterchangeIssue(
                    "RestoreRefused",
                    EIssueSeverity.Warning,
                    ex.Message,
                    Details: "A restore applies its catalog to an empty database; "
                             + "cleaning the target is a separate act performed beforehand.")]
            };
        }

        // Every section is read before any of them is applied. Objects carry their qualified-name
        // reference (Ref), so no separate name resolution is needed.
        progress?.Report(new InterchangeProgress { Stage = "Reading catalog", Percentage = 15 });
        var schemas = await catalogReader.ReadSectionAsync<SchemaDefinition>("schemas", ct);
        var extensions = await catalogReader.ReadSectionAsync<ExtensionDefinition>("extensions", ct);
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
        var nativeStages = SchemaStagePlan.PlaceNativeObjects(
            nativeObjects,
            capabilities.CanCreate,
            out var unplaceable);

        if (unplaceable.Count > 0)
        {
            throw new InvalidOperationException(
                $"The catalog holds {unplaceable.Count} object(s) this restore cannot create: "
                + $"{string.Join(", ", unplaceable.Select(n => $"{n.Kind} \"{n.Name}\""))}. Either no "
                + "stage of the format takes that kind from the native file or the adapter declares it "
                + "does not create it, and nothing was executed.");
        }

        // The stages run in the order the one plan states, which is a dependency order: an object is
        // applied once everything it names has been applied. A script over the same catalog walks the
        // same list, so the two can never disagree about what may be created before what.
        var issues = new List<InterchangeIssue>();
        var objectsSkipped = 0;

        // The two steps the restore cannot be built without: a schema holds every object, and a table is
        // what the keys, indexes, triggers and rows are applied to. A failure in either is reported as a
        // failed restore, because a target with neither the tables nor their rows did not receive the
        // catalog. Every other step is one part of the structure, and its failure costs only that part.
        var essentialStagesApplied = true;

        foreach (var step in SchemaStagePlan.Build(
                     schemas, extensions, sequences, tables, routines, views, nativeStages))
        {
            progress?.Report(new InterchangeProgress
            {
                Stage = $"Applying {step.Stage.ToString().ToLowerInvariant()}",
                Percentage = step.Percentage,
            });

            try
            {
                await writer.ApplySchemaAsync(step.Stage, step.Objects, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A stage is the commit unit the writer defines: its own transaction has already been
                // rolled back, so the stages after it still run and everything they create still lands.
                // Raising the failure here would throw all of that away for one statement the server
                // refused — an index over an operator class the target could not install, say — so the
                // failure is described in the result instead, the way the copy pipeline records a stage
                // that failed and carries on to the next one.
                _logger.LogError(
                    ex,
                    "Import stage {Stage} failed; the stages after it still run",
                    step.Stage);

                var stageName = step.Stage.ToString().ToLowerInvariant();

                // The constraint and index steps are handed the tables rather than the keys and indexes
                // themselves, so their objects are not what they create and the restore's own count of
                // applied objects holds neither kind: only a step that creates counted objects adds to
                // the skip, and the loss of a key or an index is stated in the tables it was handed
                // rather than as a figure that belongs to no object at all.
                var handedTables = step.Stage is SchemaStage.Constraints or SchemaStage.Indexes;
                if (!handedTables)
                {
                    objectsSkipped += step.Objects.Count;
                }

                essentialStagesApplied &= step.Stage is not (SchemaStage.Schemas or SchemaStage.Tables);

                var cost = handedTables
                    ? $"nothing it creates over {step.Objects.Count} table(s) is on the target"
                    : $"its {step.Objects.Count} object(s) are not on the target";

                issues.Add(new InterchangeIssue(
                    "RestoreStageFailed",
                    EIssueSeverity.Error,
                    $"The {stageName} stage could not be applied, so {cost}: {ex.Message}"));
            }
        }

        // Import data
        long totalRows = 0;
        var totalTables = tables.Count;
        var tablesProcessed = 0;
        var recordedRowCounts = new Dictionary<TableRef, long>();

        // A target with no table to receive a row is not walked table by table: one sentence says why
        // the data is missing, where a failure per table would only repeat it.
        if (!essentialStagesApplied && totalTables > 0)
        {
            issues.Add(new InterchangeIssue(
                "RestoreDataNotWritten",
                EIssueSeverity.Error,
                $"The rows of {totalTables} table(s) were not written, because the tables themselves are "
                + "not on the target."));
        }

        // A foreign key is checked on every row the moment the constraint exists, and the constraints
        // stage has already created them, so a table's rows have to follow the rows of every table they
        // name. The catalog records its tables in alphabetical order, which is no load order at all: the
        // first table whose parent sorts later would fail, and every table naming that one with it. The
        // order therefore comes from the catalog's own keys, which is the order the script path writes
        // its row blocks in.
        var loadOrder = TableLoadOrder.Order(tables);

        foreach (var table in essentialStagesApplied ? loadOrder : [])
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
            try
            {
                // The count the table's own manifest states is what the target is checked against, because
                // it was written by the capture and not by this restore. A catalog holding no data folder
                // for a table — an export run without data — recorded no count, so nothing is expected.
                // It is read before the rows are written, because the expectation has to outlast a write
                // that failed: read on the way out, it would leave exactly the tables that lost their rows
                // out of the check, and a restore that wrote nothing would be found to agree with itself.
                var recorded = await catalogReader.ReadTableRowCountAsync(schemaName, table.Name, ct);
                if (recorded is not null)
                {
                    recordedRowCounts[tableRef] = recorded.Value;
                }

                var rows = catalogReader.ReadRowsAsync(schemaName, table.Name, ct);
                var applied = await writer.WriteTableRowsAsync(tableRef, table.Columns, rows, ct);
                totalRows += applied;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One table's rows are its own: a value the target refuses, a constraint the source's
                // rows disagree with, a trigger that throws — none of them is a fact about the next
                // table, so the failure is reported against this one and the walk goes on.
                _logger.LogError(
                    ex,
                    "Import of the rows of {Table} failed; the next table still runs",
                    tableRef);

                issues.Add(new InterchangeIssue(
                    "RestoreTableDataFailed",
                    EIssueSeverity.Error,
                    $"The rows of {schemaName}.{table.Name} could not be written: {ex.Message}",
                    table.Ref));
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
            // kind the import never applies — the security section — would report a target holding
            // nothing of it as a mismatch of the restore rather than as what it is: a part of the
            // catalog no stage writes yet. The extensions are counted out for the opposite reason:
            // every database the server bootstrapped carries extensions of its own — plpgsql at the
            // least — so a target is never expected to hold exactly the catalog's number of them.
            var expectation = new TargetExpectation
            {
                ObjectCounts = BuildObjectCounts(
                    schemas,
                    tables,
                    views,
                    routines,
                    sequences,
                    nativeObjects),

                // The names travel with the counts so an adapter can tell a schema it creates from
                // one the target's own engine already keeps — the two are answered differently.
                SchemaNames = [.. schemas.Select(s => s.Name)],
                TableRowCounts = recordedRowCounts,
            };

            try
            {
                validationReport = await writer.ValidateAsync(expectation, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The restore is finished by now, so what failed is the check on it. Saying that the
                // check did not run is more honest than reporting a pass that never happened, and it
                // does not throw away a restore that already landed.
                _logger.LogError(ex, "The target could not be checked after the restore");

                issues.Add(new InterchangeIssue(
                    "RestoreValidationNotRun",
                    EIssueSeverity.Warning,
                    $"The target could not be checked after the restore: {ex.Message}"));
            }
        }

        progress?.Report(new InterchangeProgress { Stage = "Complete", Percentage = 100 });

        return new ImportResult
        {
            // The restore ran to the end unless a step it cannot be built on failed: the issues say
            // what is missing from the target, and the outcome the caller reports is "completed with
            // issues" rather than a failure that hides everything the other steps applied.
            Success = essentialStagesApplied,
            ObjectsApplied = schemas.Count + extensions.Count + tables.Count + views.Count
                + routines.Count + sequences.Count + nativeObjects.Count - objectsSkipped,
            ObjectsSkipped = objectsSkipped,
            RowsImported = totalRows,
            Issues = issues,
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

            // A sequence an identity column created is not applied as an object of its own: the column
            // that declares the identity creates it with the table. It is therefore counted where it is
            // made — at the identity columns of the tables this catalog holds — and the recorded
            // sequences are counted by the ones the restore writes itself. Counting every recorded
            // sequence would expect one the restore deliberately leaves to its column, and would expect
            // one for a table the catalog does not hold, since a store records a sequence whether or
            // not the table it belongs to was captured with it.
            [EObjectKind.Sequence] = sequences.Count(sequence => !sequence.IsIdentity)
                + tables.Sum(table => table.Columns.Count(column => !string.IsNullOrWhiteSpace(column.Identity))),
            [EObjectKind.Function] = routines.Count(r => r.Kind != EObjectKind.Procedure),
            [EObjectKind.Procedure] = routines.Count(r => r.Kind == EObjectKind.Procedure),
        };

        foreach (var group in nativeObjects.GroupBy(n => n.Kind))
        {
            counts[group.Key] = group.Count();
        }

        return counts.Where(c => c.Value > 0).ToDictionary(c => c.Key, c => c.Value);
    }
}
