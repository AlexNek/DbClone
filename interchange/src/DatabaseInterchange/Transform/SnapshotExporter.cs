using CanonicalModel.Interchange;
using DbAdapter.Definitions;
using DatabaseInterchange.Format;

using Microsoft.Extensions.Logging;

namespace DatabaseInterchange.Transform;

/// <summary>
/// Request to export a database to a catalog.
/// </summary>
public sealed record ExportRequest
{
    /// <summary>Gets or sets the source database target.</summary>
    public required DatabaseTarget Source { get; init; }

    /// <summary>Gets or sets the catalog directory path.</summary>
    public required string CatalogDirectory { get; init; }

    /// <summary>Gets or sets the export options.</summary>
    public ExportOptions Options { get; init; } = new();
}

/// <summary>
/// Result of an export operation.
/// </summary>
public sealed record ExportResult
{
    /// <summary>Gets whether the export succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the catalog directory path.</summary>
    public string CatalogDirectory { get; init; } = string.Empty;

    /// <summary>Gets the consistency mode actually used.</summary>
    public ESnapshotConsistency ConsistencyUsed { get; init; }

    /// <summary>Gets the tables that fell back to sourceOrder.</summary>
    public IReadOnlyList<TableRef> TablesWithSourceOrder { get; init; } = [];

    /// <summary>Gets the issues encountered during export.</summary>
    public IReadOnlyList<InterchangeIssue> Issues { get; init; } = [];

    /// <summary>Gets the total number of tables exported.</summary>
    public int TableCount { get; init; }

    /// <summary>Gets the total number of rows exported.</summary>
    public long RowCount { get; init; }
}

/// <summary>
/// Progress information for export/import operations.
/// </summary>
public sealed record InterchangeProgress
{
    /// <summary>Gets the current stage.</summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>Gets the progress percentage (0-100).</summary>
    public int Percentage { get; init; }

    /// <summary>Gets the current object being processed.</summary>
    public string? CurrentObject { get; init; }

    /// <summary>Gets the number of objects processed.</summary>
    public int ObjectsProcessed { get; init; }

    /// <summary>Gets the total number of objects.</summary>
    public int TotalObjects { get; init; }
}

/// <summary>
/// Exports a database to a catalog directory.
/// </summary>
public sealed class SnapshotExporter
{
    private readonly IDatabaseAdapter _adapter;
    private readonly ILogger<SnapshotExporter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotExporter"/> class.
    /// </summary>
    public SnapshotExporter(IDatabaseAdapter adapter, ILogger<SnapshotExporter> logger)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Exports a database to a catalog.
    /// </summary>
    /// <param name="request">The export request.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The export result.</returns>
    public async Task<ExportResult> ExportAsync(
        ExportRequest request,
        IProgress<InterchangeProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogInformation("Starting export to {CatalogDirectory}", request.CatalogDirectory);
        progress?.Report(new InterchangeProgress { Stage = "Starting", Percentage = 0 });

        await using var reader = _adapter.OpenReader(request.Source, request.Options);

        // Read schema
        progress?.Report(new InterchangeProgress { Stage = "Reading schema", Percentage = 10 });
        var schema = await reader.ReadSchemaAsync(ct);

        // Build the snapshot
        var snapshot = new InterchangeSnapshot
        {
            FormatVersion = InterchangeFormat.Version,
            MinimumReader = InterchangeFormat.MinimumReader,
            ProducedBy = $"DatabaseInterchange {InterchangeFormat.Version} ({_adapter.ProviderName})",
            Database = schema.Database,
            Schemas = schema.Schemas,
            Tables = schema.Tables,
            Views = schema.Views,
            Routines = schema.Routines,
            Sequences = schema.Sequences,
            Extensions = schema.Extensions,
            Relationships = schema.Relationships,
            Security = schema.Security,
        };

        // Create catalog writer
        // Interchange mode, with the source dialect recorded: the manifest's model format and
        // SourceDialect are what the import side reads to decide whether the native text it found
        // may be applied to the target. Neither may be left blank or defaulted to "direct".
        await using var writer = await CatalogWriter.CreateInterchangeAsync(
            request.CatalogDirectory,
            snapshot.ProducedBy,
            _adapter.Info.Dialect,
            overwrite: true,
            ct: ct);

        // Write schema sections
        progress?.Report(new InterchangeProgress { Stage = "Writing schemas", Percentage = 20 });
        await writer.WriteSectionAsync("schemas", snapshot.Schemas, ct);

        progress?.Report(new InterchangeProgress { Stage = "Writing tables", Percentage = 30 });
        await writer.WriteSectionAsync("tables", snapshot.Tables, ct);

        // Collect all columns
        var allColumns = snapshot.Tables.SelectMany(t => t.Columns).ToList();
        await writer.WriteSectionAsync("columns", allColumns, ct);

        progress?.Report(new InterchangeProgress { Stage = "Writing views", Percentage = 40 });
        await writer.WriteSectionAsync("views", snapshot.Views, ct);

        progress?.Report(new InterchangeProgress { Stage = "Writing routines", Percentage = 50 });
        await writer.WriteSectionAsync("routines", snapshot.Routines, ct);

        progress?.Report(new InterchangeProgress { Stage = "Writing sequences", Percentage = 60 });
        await writer.WriteSectionAsync("sequences", snapshot.Sequences, ct);

        progress?.Report(new InterchangeProgress { Stage = "Writing extensions", Percentage = 70 });
        await writer.WriteSectionAsync("extensions", snapshot.Extensions, ct);

        // The edges carry the structure the object sections cannot: which table depends on which,
        // which view reads what. Without them a restore would re-create objects in arrival order and
        // lose the ordering constraints the source stated.
        progress?.Report(new InterchangeProgress { Stage = "Writing relationships", Percentage = 71 });
        await writer.WriteRelationshipsAsync(snapshot.Relationships, ct);

        // Writes nothing when the source produced no security content: an empty section would claim
        // the database has no roles or grants, which is not something the capture checked.
        progress?.Report(new InterchangeProgress { Stage = "Writing security", Percentage = 72 });
        await writer.WriteSecurityAsync(snapshot.Security, ct);

        // Objects the neutral model has no home for travel as verbatim native SQL, so that a
        // same-dialect restore can reproduce them exactly and a cross-dialect one can refuse them
        // knowingly instead of silently dropping them.
        progress?.Report(new InterchangeProgress { Stage = "Writing native objects", Percentage = 73 });
        await writer.WriteNativeObjectsAsync(await reader.ReadNativeObjectsAsync(ct), ct);

        // Write table data
        if (request.Options.IncludeData)
        {
            var totalTables = snapshot.Tables.Count;
            var tablesProcessed = 0;
            long totalRows = 0;
            var tablesWithSourceOrder = new List<TableRef>();

            // A table row query must be qualified, and the name of the owning schema is carried by
            // the schema section: the table points at it by id.
            var schemaNames = snapshot.Schemas.ToDictionary(s => s.Ref, s => s.Name);

            foreach (var table in snapshot.Tables)
            {
                ct.ThrowIfCancellationRequested();

                var schemaName = schemaNames.TryGetValue(table.SchemaRef, out var name)
                    ? name
                    : throw new InvalidOperationException(
                        $"Table {table.Name} references schema {table.SchemaRef}, which is not in the captured schema section.");
                var tableRef = new TableRef(schemaName, table.Name);
                var rowOrder = table.RowOrder;

                _logger.LogInformation("Exporting table {Table} with row order {RowOrder}", tableRef, rowOrder);

                var rows = reader.ReadTableRowsAsync(tableRef, rowOrder, ct);
                var rowCount = await writer.WriteTableAsync(table, schemaName, rows, ct);
                totalRows += rowCount;
                tablesProcessed++;

                if (rowOrder == ERowOrder.SourceOrder)
                {
                    tablesWithSourceOrder.Add(tableRef);
                }

                var percentage = 70 + (tablesProcessed * 25 / Math.Max(totalTables, 1));
                progress?.Report(new InterchangeProgress
                {
                    Stage = "Writing data",
                    Percentage = percentage,
                    CurrentObject = table.Name,
                    ObjectsProcessed = tablesProcessed,
                    TotalObjects = totalTables,
                });
            }

            progress?.Report(new InterchangeProgress { Stage = "Completing", Percentage = 95 });

            await writer.CompleteAsync(ct);

            return new ExportResult
            {
                Success = true,
                CatalogDirectory = request.CatalogDirectory,
                ConsistencyUsed = request.Options.RequestedConsistency,
                TablesWithSourceOrder = tablesWithSourceOrder,
                TableCount = totalTables,
                RowCount = totalRows,
            };
        }

        progress?.Report(new InterchangeProgress { Stage = "Completing", Percentage = 95 });
        await writer.CompleteAsync(ct);

        progress?.Report(new InterchangeProgress { Stage = "Complete", Percentage = 100 });

        return new ExportResult
        {
            Success = true,
            CatalogDirectory = request.CatalogDirectory,
            ConsistencyUsed = request.Options.RequestedConsistency,
            TableCount = snapshot.Tables.Count,
        };
    }
}
