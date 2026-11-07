using CanonicalModel.Interchange;
using DbAdapter.Definitions;
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

        // Every section is read before any of them is applied. A catalog object names its parent by
        // id and by nothing else, and an adapter may not open a catalog to look an id up, so the
        // names behind the ids are resolved here, where the catalog is open.
        progress?.Report(new InterchangeProgress { Stage = "Reading catalog", Percentage = 15 });
        var schemas = await catalogReader.ReadSectionAsync<SchemaDefinition>("schemas", ct);
        var sequences = await catalogReader.ReadSectionAsync<SequenceDefinition>("sequences", ct);
        var tables = await catalogReader.ReadSectionAsync<TableDefinition>("tables", ct);
        var routines = await catalogReader.ReadSectionAsync<RoutineDefinition>("routines", ct);
        var views = await catalogReader.ReadSectionAsync<ViewDefinition>("views", ct);
        var names = BuildNameMap(schemas, tables);

        // The stages run in the order SchemaStage declares, which is a dependency order: an object is
        // applied once everything it names has been applied. Routines come before views because a
        // view may call a function.
        progress?.Report(new InterchangeProgress { Stage = "Applying schemas", Percentage = 20 });
        await writer.ApplySchemaAsync(SchemaStage.Schemas, AsObjects(schemas), names, ct);

        progress?.Report(new InterchangeProgress { Stage = "Applying sequences", Percentage = 30 });
        await writer.ApplySchemaAsync(SchemaStage.Sequences, AsObjects(sequences), names, ct);

        progress?.Report(new InterchangeProgress { Stage = "Applying tables", Percentage = 40 });
        await writer.ApplySchemaAsync(SchemaStage.Tables, AsObjects(tables), names, ct);

        progress?.Report(new InterchangeProgress { Stage = "Applying routines", Percentage = 50 });
        await writer.ApplySchemaAsync(SchemaStage.Routines, AsObjects(routines), names, ct);

        progress?.Report(new InterchangeProgress { Stage = "Applying views", Percentage = 60 });
        await writer.ApplySchemaAsync(SchemaStage.Views, AsObjects(views), names, ct);

        // Import data
        long totalRows = 0;
        var totalTables = tables.Count;
        var tablesProcessed = 0;

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogInformation("Importing data for table {Table}", table.Name);

            // A table the map cannot name would be addressed by bare name and land in whichever
            // schema the connection happens to search first, so its rows are not read at all.
            var tableRef = names.Table(table.Id)
                ?? throw new InvalidOperationException(
                    $"The catalog does not name the schema the table \"{table.Name}\" lives in, so "
                    + "its rows cannot be addressed.");

            var rows = catalogReader.ReadRowsAsync(table.Id.ToString()!, ct);
            await writer.WriteTableRowsAsync(tableRef, rows, ct);

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

            var expectation = new TargetExpectation
            {
                ObjectCounts = new Dictionary<EObjectKind, int>
                {
                    [EObjectKind.Schema] = schemas.Count,
                    [EObjectKind.Table] = tables.Count,
                    [EObjectKind.View] = views.Count,
                    [EObjectKind.Sequence] = sequences.Count,
                },
            };

            validationReport = await writer.ValidateAsync(expectation, ct);
        }

        progress?.Report(new InterchangeProgress { Stage = "Complete", Percentage = 100 });

        return new ImportResult
        {
            Success = true,
            ObjectsApplied = schemas.Count + tables.Count + views.Count + routines.Count + sequences.Count,
            RowsImported = totalRows,
            ValidationReport = validationReport,
        };
    }

    /// <summary>
    /// Resolves the names the ids inside this catalog stand for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table whose schema id no schema answers is left out of the map on purpose. The alternative —
    /// reading the name as empty — addresses the object to the connection's search path, which puts
    /// the object in a schema the source never named it in. Leaving it out makes the adapter refuse
    /// it, which is the honest outcome for a catalog that cannot describe its own contents.
    /// </para>
    /// <para>
    /// An id is derived from the object's kind, schema, name and parent, so a repeated id repeats the
    /// same name and last-one-wins cannot hide a conflict.
    /// </para>
    /// </remarks>
    private static ObjectNameMap BuildNameMap(
        IReadOnlyList<SchemaDefinition> schemas,
        IReadOnlyList<TableDefinition> tables)
    {
        var schemaNames = new Dictionary<InterchangeId, string>();
        foreach (var schema in schemas)
        {
            schemaNames[schema.Id] = schema.Name;
        }

        var tableRefs = new Dictionary<InterchangeId, TableRef>();
        foreach (var table in tables)
        {
            if (schemaNames.TryGetValue(table.SchemaId, out var schemaName))
            {
                tableRefs[table.Id] = new TableRef(schemaName, table.Name);
            }
        }

        return new ObjectNameMap(schemaNames, tableRefs);
    }

    private static IReadOnlyList<ISchemaObject> AsObjects<T>(IReadOnlyList<T> objects)
        where T : ISchemaObject =>
        [.. objects];
}
