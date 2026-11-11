using CanonicalModel.Interchange;
using DbAdapter.Definitions;
using DatabaseInterchange.Format;
using DatabaseInterchange.Report;
using DatabaseInterchange.Script;

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

    /// <summary>Gets the data bytes of the catalog: row chunks plus blob containers (D3, FR3).</summary>
    public long DataBytes { get; init; }

    /// <summary>Gets the metadata bytes of the catalog, the two root JSON files included (D3, FR3).</summary>
    public long MetadataBytes { get; init; }

    /// <summary>Gets the size of the catalog artifact: data plus metadata (D3). Excludes <c>summary.md</c>.</summary>
    public long CatalogBytes { get; init; }

    /// <summary>Gets the number of row-chunk files written.</summary>
    public int TotalChunks { get; init; }

    /// <summary>Gets the schema sections the writer finished with, in the order it wrote them (D3).</summary>
    public IReadOnlyList<string> SectionNames { get; init; } = [];

    /// <summary>
    /// Gets the report's path relative to <see cref="CatalogDirectory"/>, or <see langword="null"/> when
    /// the option was off, the write failed, or the user cancelled during that stage — in which case the
    /// rest of this result still describes the finished catalog (D4.2).
    /// </summary>
    public string? ReportRelativePath { get; init; }

    /// <summary>
    /// Gets what the SQL script pass produced, or <see langword="null"/> when the option was off. A refusal
    /// or a failed write leaves the catalog complete and restorable, and states its reason here and as one
    /// warning issue (FR8, D8).
    /// </summary>
    public ScriptResult? SqlScripts { get; init; }
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

    // The report pipeline is composed here rather than injected: the product uses no container, and all
    // three collaborators are internal to it, so a consumer of the package never sees these types (D2).
    private readonly CatalogStatisticsBuilder _statisticsBuilder = new();
    private readonly RelationGraphBuilder _relationBuilder = new();
    private readonly CatalogReportWriter _reportWriter = new();
    private readonly CatalogScriptWriter _scriptWriter = new();

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

            // Resolve table folder names per schema BEFORE writing any data. This is the fix for
            // the §2.4 silent table loss: on a case-folding filesystem, MixedCase and mixedcase
            // would map to the same directory, and the second write would overwrite the first.
            // ResolveCollisions gives the loser a discriminator suffix so both survive.
            var resolvedTableNames = new Dictionary<TableRef, string>();
            var tablesBySchema = snapshot.Tables
                .GroupBy(t => t.SchemaRef)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (schemaRef, tables) in tablesBySchema)
            {
                if (!schemaNames.TryGetValue(schemaRef, out var schemaName))
                {
                    continue;
                }

                var rawNames = tables.Select(t => t.Name).ToList();
                var resolved = NameEncoder.ResolveCollisions(
                    rawNames,
                    name => CanonicalModel.Interchange.ObjectRef.Table(schemaName, name));

                foreach (var (rawName, encodedName) in resolved)
                {
                    resolvedTableNames[new TableRef(schemaName, rawName)] = encodedName;
                }
            }

            foreach (var table in snapshot.Tables)
            {
                ct.ThrowIfCancellationRequested();

                var schemaName = schemaNames.TryGetValue(table.SchemaRef, out var name)
                    ? name
                    : throw new InvalidOperationException(
                        $"Table {table.Name} references schema {table.SchemaRef}, which is not in the captured schema section.");
                var tableRef = new TableRef(schemaName, table.Name);

                // When the caller named specific tables, skip the rest.
                if (request.Options.Tables.Count > 0
                    && !request.Options.Tables.Any(t =>
                        string.Equals(t.Schema, schemaName, StringComparison.Ordinal)
                        && string.Equals(t.Name, table.Name, StringComparison.Ordinal)))
                {
                    tablesProcessed++;
                    continue;
                }

                // Skip tables the caller excluded.
                if (request.Options.ExcludedTables.Contains(tableRef))
                {
                    tablesProcessed++;
                    continue;
                }

                var rowOrder = table.RowOrder;

                _logger.LogInformation("Exporting table {Table} with row order {RowOrder}", tableRef, rowOrder);

                var rows = reader.ReadTableRowsAsync(tableRef, rowOrder, ct);

                // Pass the pre-resolved folder name so the writer can detect collisions.
                resolvedTableNames.TryGetValue(tableRef, out var resolvedName);
                var rowCount = await writer.WriteTableAsync(
                    table, schemaName, rows, request.Options, ct, resolvedTableName: resolvedName);
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

            return await CompleteAndReportAsync(
                request, snapshot, writer, tablesWithSourceOrder, totalRows, progress, ct);
        }

        return await CompleteAndReportAsync(
            request, snapshot, writer, [], rowCount: 0, progress, ct);
    }

    /// <summary>
    /// Closes the catalog, writes the summary if it was asked for, and builds the result from the numbers
    /// the writer finished with (D4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the point of the method: <c>manifest.json</c> is closed first, so nothing that follows can
    /// perturb the catalog's content identity and can state the totals the writer actually reached. That line
    /// is also the cancellation boundary of D4.2 — a cancel before it makes the store genuinely incomplete and
    /// propagates; a cancel after it stops only the scripts and the report, because the artifact on disk is
    /// finished, verifiable and restorable. The scripts come before the totals because a script file is part of
    /// what the catalog holds: a measurement taken first would describe a directory a stage later changed.
    /// </para>
    /// <para>
    /// The progress frames stay monotone on both paths: <c>Completing / 95</c>, then <c>Writing SQL scripts</c>
    /// at 96 when the option is on, then <c>Writing summary</c> at 97 when the report runs, then
    /// <c>Complete / 100</c> last. Nothing is reported after the summary.
    /// </para>
    /// <para>
    /// <paramref name="sourceOrderTables"/> are the tables actually written in source order. The schema-only
    /// path has none, because it wrote no rows: the request's default row order would name tables the
    /// catalog never captured.
    /// </para>
    /// </remarks>
    private async Task<ExportResult> CompleteAndReportAsync(
        ExportRequest request,
        InterchangeSnapshot snapshot,
        CatalogWriter writer,
        IReadOnlyList<TableRef> sourceOrderTables,
        long rowCount,
        IProgress<InterchangeProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new InterchangeProgress { Stage = "Completing", Percentage = 95 });
        await writer.CompleteAsync(ct);

        // A script-write failure, a refusal or a report-write failure lands here as one warning issue and
        // never fails the store (FR8, FR13); a cancellation in either stage is a stop, not a failure, and
        // adds no issue (D4.2, D12).
        var issues = new List<InterchangeIssue>();

        // The relation facts are analysed once for whichever pass asked for them: the scripts name the
        // cycles and the report describes them, and analysing the same snapshot twice gives one answer twice.
        var relations = request.Options.IncludeSqlScripts || request.Options.IncludeCatalogReport
            ? _relationBuilder.Build(snapshot)
            : null;

        // The scripts are a post-catalog pass: manifest.json is closed, so they describe the catalog on
        // disk rather than a source database that may have moved on (D8).
        var scripts = await WriteSqlScriptsAsync(request, relations, issues, progress, ct);

        // The totals are read after the scripts, because a script file is part of what the catalog holds: a
        // measurement taken before the pass would state a directory that does not exist yet (D8).
        var statistics = BuildStatistics(request, snapshot, writer, sourceOrderTables);

        var reportPath = await WriteCatalogReportAsync(
            request, statistics, relations, scripts, issues, progress, ct);

        progress?.Report(new InterchangeProgress { Stage = "Complete", Percentage = 100 });

        return new ExportResult
        {
            Success = true,
            CatalogDirectory = request.CatalogDirectory,
            ConsistencyUsed = request.Options.RequestedConsistency,
            TablesWithSourceOrder = sourceOrderTables,
            TableCount = snapshot.Tables.Count,
            RowCount = rowCount,
            DataBytes = statistics.DataBytes,
            MetadataBytes = statistics.MetadataBytes,
            CatalogBytes = statistics.CatalogBytes,
            TotalChunks = (int)statistics.ChunkFiles,
            SectionNames = statistics.SectionNames,
            Issues = issues,
            ReportRelativePath = reportPath,
            SqlScripts = scripts,
        };
    }

    /// <summary>
    /// Writes <c>sql/schema.sql</c> and <c>sql/data.sql</c> when the caller asked for them (D8).
    /// </summary>
    /// <remarks>
    /// The whole pass is contained: a failure the writer does not expect — a renderer this adapter cannot
    /// build, a catalog the reader rejects — is one warning and no file, because a catalog without the scripts
    /// is still a complete catalog and the store has already succeeded (FR8).
    /// </remarks>
    /// <returns>
    /// What the pass produced, or <see langword="null"/> when the option was off — a refusal or a failed
    /// write still answers with a result that states why nothing was written.
    /// </returns>
    private async Task<ScriptResult?> WriteSqlScriptsAsync(
        ExportRequest request,
        RelationMap? relations,
        IList<InterchangeIssue> issues,
        IProgress<InterchangeProgress>? progress,
        CancellationToken ct)
    {
        if (!request.Options.IncludeSqlScripts)
        {
            return null;
        }

        // A catalog this pass is about to describe must not look finished while the pass is running (D8).
        progress?.Report(new InterchangeProgress { Stage = "Writing SQL scripts", Percentage = 96 });

        try
        {
            return await _scriptWriter.WriteAsync(
                request.CatalogDirectory,
                _adapter.CreateScriptRenderer(),
                relations?.CyclicGroups ?? [],
                issues,
                ct);
        }
        catch (OperationCanceledException)
        {
            // A cancel is a stop, not a failure, and adds no issue (D4.2, D12).
            return new ScriptResult { Cancelled = true };
        }
        catch (Exception ex)
        {
            var reason = $"The SQL scripts could not be written: {ex.Message}";

            issues.Add(new InterchangeIssue(
                CatalogScriptWriter.WriteFailureCode,
                EIssueSeverity.Warning,
                reason));

            return new ScriptResult
            {
                Refusal = new ScriptRefusal
                {
                    Code = CatalogScriptWriter.WriteFailureCode,
                    Reason = reason,
                },
            };
        }
    }

    /// <summary>
    /// Writes <c>summary.md</c> into the catalog root when the caller asked for it (D4).
    /// </summary>
    /// <returns>
    /// The report's relative path, or <see langword="null"/> when the option is off, the write failed, or
    /// the stage was cancelled.
    /// </returns>
    private async Task<string?> WriteCatalogReportAsync(
        ExportRequest request,
        CatalogStatistics statistics,
        RelationMap? relations,
        ScriptResult? scripts,
        IList<InterchangeIssue> issues,
        IProgress<InterchangeProgress>? progress,
        CancellationToken ct)
    {
        if (!request.Options.IncludeCatalogReport || relations is null)
        {
            return null;
        }

        // The report comes last, so the numbers it states are the ones the catalog reached — the scripts
        // above are described by it rather than measured into it (D8, D10).
        progress?.Report(new InterchangeProgress { Stage = "Writing summary", Percentage = 97 });

        return await _reportWriter.WriteAsync(
            request.CatalogDirectory,
            statistics,
            relations,
            scripts,
            issues,
            ct);
    }

    /// <summary>
    /// Joins the captured model with the writer's own measurements (D3) into the report's statistics.
    /// </summary>
    /// <remarks>
    /// Built whether or not the report is requested, because the three byte measures of FR3 and the real
    /// counters of FR12 are the writer's facts and the result must carry them either way. Composing them
    /// here rather than re-adding them in the result is what keeps the report and the result stating the
    /// same numbers.
    /// </remarks>
    private CatalogStatistics BuildStatistics(
        ExportRequest request,
        InterchangeSnapshot snapshot,
        CatalogWriter writer,
        IReadOnlyList<TableRef> sourceOrderTables) =>
        _statisticsBuilder.Build(
            snapshot,
            writer.TableStatistics,
            writer.MetadataFileSizes,
            writer.RootJsonBytes,
            writer.SectionNames,
            writer.ExportedAt,
            _adapter.Info.Dialect,
            ReportText.Words(request.Options.RequestedConsistency.ToString()),
            request.Options.ExcludedTables.Count,
            sourceOrderTables);
}
