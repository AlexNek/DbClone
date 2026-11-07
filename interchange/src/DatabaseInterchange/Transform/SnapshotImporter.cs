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

        // Read and apply schema sections
        progress?.Report(new InterchangeProgress { Stage = "Applying schemas", Percentage = 20 });
        var schemas = await catalogReader.ReadSectionAsync<SchemaDefinition>("schemas", ct);
        await writer.ApplySchemaAsync(SchemaStage.Schemas, schemas.Cast<ISchemaObject>().ToList(), ct);

        progress?.Report(new InterchangeProgress { Stage = "Applying sequences", Percentage = 30 });
        var sequences = await catalogReader.ReadSectionAsync<SequenceDefinition>("sequences", ct);
        await writer.ApplySchemaAsync(SchemaStage.Sequences, sequences.Cast<ISchemaObject>().ToList(), ct);

        progress?.Report(new InterchangeProgress { Stage = "Applying tables", Percentage = 40 });
        var tables = await catalogReader.ReadSectionAsync<TableDefinition>("tables", ct);
        await writer.ApplySchemaAsync(SchemaStage.Tables, tables.Cast<ISchemaObject>().ToList(), ct);

        progress?.Report(new InterchangeProgress { Stage = "Applying views", Percentage = 50 });
        var views = await catalogReader.ReadSectionAsync<ViewDefinition>("views", ct);
        await writer.ApplySchemaAsync(SchemaStage.Views, views.Cast<ISchemaObject>().ToList(), ct);

        progress?.Report(new InterchangeProgress { Stage = "Applying routines", Percentage = 60 });
        var routines = await catalogReader.ReadSectionAsync<RoutineDefinition>("routines", ct);
        await writer.ApplySchemaAsync(SchemaStage.Routines, routines.Cast<ISchemaObject>().ToList(), ct);

        // Import data
        long totalRows = 0;
        var totalTables = tables.Count;
        var tablesProcessed = 0;

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogInformation("Importing data for table {Table}", table.Name);

            var tableRef = new TableRef(string.Empty, table.Name);
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
}
