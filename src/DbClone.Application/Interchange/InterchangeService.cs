using System.Diagnostics;

using CanonicalModel.Interchange;

using DatabaseInterchange.Format;
using DatabaseInterchange.Transform;

using DbAdapter.Definitions;

using DbClone.Application.DTOs;
using DbClone.Application.Targets;

using Microsoft.Extensions.Logging;

using DbClone.Application.Relationship;

namespace DbClone.Application.Interchange;

/// <summary>
/// Default implementation of <see cref="IInterchangeService"/>.
/// Builds a <see cref="DbAdapter.Definitions.DatabaseTarget"/> through <see cref="DatabaseTargetFactory"/>
/// and delegates to the interchange product for export, import, inspection, and verification.
/// </summary>
public sealed class InterchangeService : IInterchangeService
{
    private readonly DatabaseTargetFactory _targetFactory;
    private readonly ILogger<InterchangeService> _logger;

    public InterchangeService(
        DatabaseTargetFactory targetFactory,
        ILogger<InterchangeService> logger)
    {
        _targetFactory = targetFactory ?? throw new ArgumentNullException(nameof(targetFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<InterchangeStoreResult> StoreAsync(
        InterchangeStoreRequest request,
        IProgress<InterchangeProgressView>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();
        var target = _targetFactory.Create(request.Connection);
        var adapter = _targetFactory.ResolveAdapter(request.Connection.Engine);

        _logger.LogInformation(
            "Store started: catalog={CatalogPath} tables={TableCount} includeData={IncludeData}",
            request.CatalogPath,
            request.TableSelection.ExcludedTables.Count,
            request.IncludeData);

        try
        {
            var runtime = DatabaseInterchangeRuntime.Create(adapter);
            var exporter = runtime.CreateExporter();

            var exportOptions = new ExportOptions
            {
                IncludeData = request.IncludeData,
                ChunkBytes = request.ChunkBytes,
                MaxRowsPerChunk = request.RowsPerChunk,
                RequestedConsistency = Enum.TryParse<ESnapshotConsistency>(
                    request.SnapshotConsistency, ignoreCase: true, out var consistency)
                    ? consistency
                    : ESnapshotConsistency.ConsistentSnapshot,
                ExcludedTables = request.TableSelection.ExcludedTables
                    .Select(t => new TableRef(t.Schema, t.Name))
                    .ToHashSet(),
                IncludeCatalogReport = request.IncludeSummaryReport,
                IncludeSqlScripts = request.IncludeSqlScripts,

                // The one conversion from the unit a user picks to the unit the product reasons with: the
                // product never sees a megabyte, and the request never carries a byte (D11).
                SqlScriptMaxDataFileBytes = request.SqlScriptsMaxFileMb * 1024L * 1024L,
            };

            var exportRequest = new ExportRequest
            {
                Source = target,
                CatalogDirectory = request.CatalogPath,
                Options = exportOptions,
            };

            var progressAdapter = progress is null ? null : new Progress<InterchangeProgress>(p =>
                progress.Report(new InterchangeProgressView(
                    p.Stage,
                    p.ObjectsProcessed,
                    p.TotalObjects,
                    p.CurrentObject ?? string.Empty,
                    0, // RowsDone - not tracked in product progress
                    0, // RowsTotal - not tracked in product progress
                    0, // BytesDone - not tracked in product progress
                    0, // BytesTotal - not tracked in product progress
                    TimeSpan.Zero, // Elapsed - not tracked in product progress
                    null))); // Remaining - not tracked in product progress

            var result = await exporter.ExportAsync(exportRequest, progressAdapter, cancellationToken);

            sw.Stop();

            // Verify if requested
            bool verificationPassed = true;
            if (request.VerifyAfterStore)
            {
                var verifier = new CatalogVerifier();
                var verification = await verifier.VerifyAsync(request.CatalogPath, cancellationToken);
                verificationPassed = verification.IsValid;
            }

            var outcome = result.Success
                ? (result.Issues.Count > 0 ? EStoreOutcome.CompletedWithIssues : EStoreOutcome.Success)
                : EStoreOutcome.Failed;

            return new InterchangeStoreResult(
                Outcome: outcome,
                SectionsWritten: result.SectionNames.Count,
                TablesExported: result.TableCount,
                RowsExported: result.RowCount,
                BytesExported: result.CatalogBytes,
                ChunksWritten: result.TotalChunks,
                IsComplete: result.Success,
                ConsistencyModeUsed: result.ConsistencyUsed.ToString(),
                TablesThatFellBackToSourceOrder: result.TablesWithSourceOrder.Select(t => t.ToString()).ToList(),
                VerificationPassed: verificationPassed,
                Issues: result.Issues.Select(MapIssue).ToList(),
                CatalogPath: result.CatalogDirectory,
                Elapsed: sw.Elapsed,

                // The product returns the report's name relative to the catalog it was handed, so this
                // wrapper is the one place that turns it into a path. The app knows no catalog file name.
                SummaryPath: result.ReportRelativePath is null
                    ? null
                    : Path.Combine(result.CatalogDirectory, result.ReportRelativePath),

                // The same rule for the scripts, which the product names once per file: this wrapper is the
                // one place that knows where the catalog folder is, so it forms every absolute path here.
                SqlScripts: result.SqlScripts is { } scripts
                    ? new SqlScriptPaths(
                        SchemaPath: ToAbsolute(result.CatalogDirectory, scripts.SchemaPath),
                        DataFiles: scripts.DataFiles
                            .Select(file => new SqlScriptFile(
                                Path.Combine(result.CatalogDirectory, file.RelativePath),
                                file.Bytes,
                                file.Statements,
                                file.Rows,
                                file.IsPartOf))
                            .ToList(),
                        LoadPath: ToAbsolute(result.CatalogDirectory, scripts.LoadPath),
                        SchemaBytes: scripts.SchemaBytes,
                        SchemaStatements: scripts.SchemaStatements,
                        Rows: scripts.Rows,
                        PayloadBytes: scripts.PayloadBytes)
                    : null);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning("Store cancelled: catalog={CatalogPath}", request.CatalogPath);
            return new InterchangeStoreResult(
                Outcome: EStoreOutcome.Incomplete,
                SectionsWritten: 0,
                TablesExported: 0,
                RowsExported: 0,
                BytesExported: 0,
                ChunksWritten: 0,
                IsComplete: false,
                ConsistencyModeUsed: string.Empty,
                TablesThatFellBackToSourceOrder: [],
                VerificationPassed: false,
                Issues: [new InterchangeIssueView(EInterchangeSeverity.Warning, "Cancelled", "The operation was cancelled.", string.Empty, string.Empty)],
                CatalogPath: null,
                Elapsed: sw.Elapsed,
                SummaryPath: null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Store failed: catalog={CatalogPath}", request.CatalogPath);
            return new InterchangeStoreResult(
                Outcome: EStoreOutcome.Failed,
                SectionsWritten: 0,
                TablesExported: 0,
                RowsExported: 0,
                BytesExported: 0,
                ChunksWritten: 0,
                IsComplete: false,
                ConsistencyModeUsed: string.Empty,
                TablesThatFellBackToSourceOrder: [],
                VerificationPassed: false,
                Issues: [new InterchangeIssueView(EInterchangeSeverity.Error, "ExportFailed", ex.Message, string.Empty, string.Empty)],
                CatalogPath: null,
                Elapsed: sw.Elapsed,
                SummaryPath: null);
        }
    }

    /// <inheritdoc />
    public async Task<InterchangePlanView> PlanRestoreAsync(
        string catalogPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);

        _logger.LogInformation("Plan restore started: catalog={CatalogPath}", catalogPath);

        // Open catalog to read metadata — no adapter needed for planning
        await using var reader = await CatalogReader.OpenAsync(catalogPath, cancellationToken);

        var schemas = await reader.ReadSectionAsync<SchemaDefinition>("schemas", cancellationToken);
        var tables = await reader.ReadSectionAsync<TableDefinition>("tables", cancellationToken);
        var views = await reader.ReadSectionAsync<ViewDefinition>("views", cancellationToken);
        var routines = await reader.ReadSectionAsync<RoutineDefinition>("routines", cancellationToken);
        var sequences = await reader.ReadSectionAsync<SequenceDefinition>("sequences", cancellationToken);

        var totalObjects = schemas.Count + tables.Count + views.Count + routines.Count + sequences.Count;
        var stages = 6; // schemas, sequences, tables, views, routines, data

        return new InterchangePlanView(
            Stages: stages,
            ObjectCount: totalObjects,
            RefusedObjectCount: 0, // TODO: Check adapter capabilities when available
            RefusedObjects: [], // TODO: Check adapter capabilities when available
            DependencyCycles: []); // TODO: Implement cycle detection when DependencyPlan is available
    }

    /// <inheritdoc />
    public async Task<InterchangeRestoreResult> RestoreAsync(
        InterchangeRestoreRequest request,
        IProgress<InterchangeProgressView>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();
        var target = _targetFactory.Create(request.Connection);
        var adapter = _targetFactory.ResolveAdapter(request.Connection.Engine);

        _logger.LogInformation(
            "Restore started: catalog={CatalogPath} target={Dialect}",
            request.CatalogPath,
            target.Dialect);

        try
        {
            var runtime = DatabaseInterchangeRuntime.Create(adapter);
            var importer = runtime.CreateImporter();

            var importOptions = new ImportOptions
            {
                ValidateAfterImport = true,
                UnsupportedFeaturePolicy = Enum.TryParse<EUnsupportedFeaturePolicy>(
                    request.UnsupportedFeaturePolicy, ignoreCase: true, out var policy)
                    ? policy
                    : EUnsupportedFeaturePolicy.Fail,
            };

            var importRequest = new ImportRequest
            {
                CatalogDirectory = request.CatalogPath,
                Target = target,
                Options = importOptions,
            };

            var progressAdapter = progress is null ? null : new Progress<InterchangeProgress>(p =>
                progress.Report(new InterchangeProgressView(
                    p.Stage,
                    p.ObjectsProcessed,
                    p.TotalObjects,
                    p.CurrentObject ?? string.Empty,
                    0, // RowsDone - not tracked in product progress
                    0, // RowsTotal - not tracked in product progress
                    0, // BytesDone - not tracked in product progress
                    0, // BytesTotal - not tracked in product progress
                    TimeSpan.Zero, // Elapsed - not tracked in product progress
                    null))); // Remaining - not tracked in product progress

            var result = await importer.ImportAsync(importRequest, progressAdapter, cancellationToken);

            sw.Stop();

            return BuildRestoreResult(result, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning("Restore cancelled: catalog={CatalogPath}", request.CatalogPath);
            return new InterchangeRestoreResult(
                Outcome: ERestoreOutcome.Cancelled,
                ObjectsApplied: 0,
                ObjectsSkipped: 0,
                RowsRestored: 0,
                RowCountMismatches: 0,
                Issues: [new InterchangeIssueView(EInterchangeSeverity.Warning, "Cancelled", "The operation was cancelled.", string.Empty, string.Empty)],
                Validation: null,
                LastCompletedChunk: null,
                Elapsed: sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Restore failed: catalog={CatalogPath}", request.CatalogPath);
            return new InterchangeRestoreResult(
                Outcome: ERestoreOutcome.Failed,
                ObjectsApplied: 0,
                ObjectsSkipped: 0,
                RowsRestored: 0,
                RowCountMismatches: 0,
                Issues: [new InterchangeIssueView(EInterchangeSeverity.Error, "ImportFailed", ex.Message, string.Empty, string.Empty)],
                Validation: null,
                LastCompletedChunk: null,
                Elapsed: sw.Elapsed);
        }
    }

    /// <inheritdoc />
    public async Task<CatalogSummary> InspectAsync(
        string catalogPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);

        _logger.LogInformation("Inspect started: catalog={CatalogPath}", catalogPath);

        await using var reader = await CatalogReader.OpenAsync(catalogPath, cancellationToken);
        var manifest = reader.Manifest;
        var index = reader.Index;

        // Read sections to count objects
        var schemas = await reader.ReadSectionAsync<SchemaDefinition>("schemas", cancellationToken);
        var tables = await reader.ReadSectionAsync<TableDefinition>("tables", cancellationToken);
        var views = await reader.ReadSectionAsync<ViewDefinition>("views", cancellationToken);
        var routines = await reader.ReadSectionAsync<RoutineDefinition>("routines", cancellationToken);
        var sequences = await reader.ReadSectionAsync<SequenceDefinition>("sequences", cancellationToken);
        var extensions = await reader.ReadSectionAsync<ExtensionDefinition>("extensions", cancellationToken);

        // R9: Inspection never re-reads data. Row counts come from the manifest, not from iterating rows.
        // The manifest's counts are recorded during the write pass (R8), so they are truthful.
        var totalRows = manifest.Counts.Rows;
        var totalChunks = manifest.Counts.Chunks;

        // Count preservation levels
        var preservationCounts = new Dictionary<string, int>
        {
            ["Full"] = tables.Count + views.Count + routines.Count,
            ["NativeOnly"] = 0,
            ["Omitted"] = 0,
        };

        // Count security objects
        var securityCounts = new Dictionary<string, int>
        {
            ["Roles"] = 0,
            ["Ownerships"] = 0,
            ["Policies"] = 0,
            ["Grants"] = 0,
        };

        var producerIssues = new List<InterchangeIssueView>(); // Manifest doesn't carry producer issues in v1

        await Task.CompletedTask;

        return new CatalogSummary(
            FormatVersion: manifest.FormatVersion,
            Producer: manifest.Producer,
            ExportTime: null, // Manifest doesn't carry export time in v1
            IsComplete: manifest.Complete,
            Sections: index.Sections.Count,
            Tables: index.TableCount,
            Rows: totalRows,
            Chunks: totalChunks,
            Bytes: manifest.Files.Sum(f => f.Bytes),
            PreservationCounts: preservationCounts,
            SecurityCounts: securityCounts,
            ProducerIssues: producerIssues);
    }

    /// <inheritdoc />
    public async Task<RelationshipGraphView> ReadGraphAsync(
        string catalogPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);

        _logger.LogInformation("Read graph started: catalog={CatalogPath}", catalogPath);

        await using var reader = await CatalogReader.OpenAsync(catalogPath, cancellationToken);
        var manifest = reader.Manifest;
        var index = reader.Index;

        var schemas = await reader.ReadSectionAsync<SchemaDefinition>("schemas", cancellationToken);
        var tables = await reader.ReadSectionAsync<TableDefinition>("tables", cancellationToken);

        // Index versus content disagreement: draw neither and stop with both counts, because a picture
        // of the smaller set would understate relations and look authoritative while doing it.
        if (index.TableCount != tables.Count)
        {
            throw new InvalidOperationException(
                $"The catalog contradicts itself: catalog.json records {index.TableCount} table(s) " +
                $"while schema/tables.json holds {tables.Count}. Re-export the database.");
        }

        var schemaNameById = new Dictionary<string, string>(schemas.Count, StringComparer.Ordinal);
        foreach (var schema in schemas)
        {
            schemaNameById[schema.Ref.Value] = schema.Name;
        }

        string SchemaNameOf(TableDefinition table) =>
            schemaNameById.TryGetValue(table.SchemaRef.Value, out var name)
                ? name
                : SafeObjectName(table.SchemaRef);

        var capturedById = new Dictionary<string, TableDefinition>(tables.Count, StringComparer.Ordinal);
        foreach (var table in tables)
        {
            capturedById[table.Ref.Value] = table;
        }

        // R9: rows come from each table's table.json, never from re-reading a data row. A null means
        // the run captured no data for that table; a 0 means it captured the table and it is empty.
        var rowCountById = new Dictionary<string, long?>(tables.Count, StringComparer.Ordinal);
        foreach (var table in tables)
        {
            rowCountById[table.Ref.Value] = await reader.ReadTableRowCountAsync(
                SchemaNameOf(table),
                table.Name,
                cancellationToken);
        }

        var edges = new List<RelationshipEdgeView>();
        var outsideById = new Dictionary<string, RelationshipOutsideTableView>(StringComparer.Ordinal);
        var referencingColumnsByTable = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var referencedColumnsByTable = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var table in tables)
        {
            var childId = table.Ref.Value;
            var columnsByName = new Dictionary<string, ColumnDefinition>(table.Columns.Count, StringComparer.Ordinal);
            foreach (var column in table.Columns)
            {
                columnsByName[column.Name] = column;
            }

            foreach (var foreignKey in table.ForeignKeys)
            {
                var parentRef = foreignKey.ReferencedTableRef;
                var stated = DescribeReference(parentRef);
                var parentIsCaptured = stated.StatesSomething && capturedById.ContainsKey(parentRef.Value);

                string parentId;
                if (stated.StatesSomething)
                {
                    parentId = parentRef.Value;
                    if (!parentIsCaptured)
                    {
                        outsideById.TryAdd(parentId, new RelationshipOutsideTableView(parentId, stated.Schema, stated.Name));
                    }
                }
                else
                {
                    // The reference states nothing. The key only keeps two nameless targets apart;
                    // the label is the renderer's business, never a name invented here.
                    parentId = $"unresolved:{childId}|{foreignKey.Name}";
                    outsideById.TryAdd(parentId, new RelationshipOutsideTableView(parentId, null, null));
                }

                edges.Add(new RelationshipEdgeView(
                    ChildId: childId,
                    ParentId: parentId,
                    ConstraintName: foreignKey.Name,
                    Columns: [.. foreignKey.Columns],
                    ReferencedColumns: [.. foreignKey.ReferencedColumns],
                    OnDelete: NormalizeAction(foreignKey.OnDelete),
                    OnUpdate: NormalizeAction(foreignKey.OnUpdate),
                    IsMandatory: IsMandatory(columnsByName, foreignKey.Columns),
                    IsSelfReference: parentIsCaptured && string.Equals(parentId, childId, StringComparison.Ordinal),
                    ParentIsCaptured: parentIsCaptured));

                var referencing = GetOrAdd(referencingColumnsByTable, childId);
                foreach (var column in foreignKey.Columns)
                {
                    referencing.Add(column);
                }

                if (parentIsCaptured)
                {
                    var referenced = GetOrAdd(referencedColumnsByTable, parentId);
                    foreach (var column in foreignKey.ReferencedColumns)
                    {
                        referenced.Add(column);
                    }
                }
            }
        }

        var tableViews = tables
            .OrderBy(t => t.Ref.Value, StringComparer.Ordinal)
            .Select(table =>
            {
                var id = table.Ref.Value;
                referencingColumnsByTable.TryGetValue(id, out var referencing);
                referencedColumnsByTable.TryGetValue(id, out var referenced);

                var columns = table.Columns
                    .OrderBy(c => c.Ordinal)
                    .Select(c => new RelationshipColumnView(
                        Name: c.Name,
                        LogicalType: c.LogicalType.ToString(),
                        IsNullable: c.Nullable,
                        IsPartOfPrimaryKey: table.PrimaryKey?.Columns.Contains(c.Name, StringComparer.Ordinal) == true,
                        IsReferencingForeignKey: referencing?.Contains(c.Name) == true,
                        IsReferencedByForeignKey: referenced?.Contains(c.Name) == true,
                        Comment: c.Description))
                    .ToList();

                var indexes = (table.Indexes ?? [])
                    .OrderBy(index => index.Name, StringComparer.Ordinal)
                    .Select(index => new RelationshipIndexView(
                        Name: index.Name,
                        IsUnique: index.IsUnique,
                        Definition: index.NativeDefinition?.Text,
                        Columns: index.Columns))
                    .ToList();

                return new RelationshipTableView(
                    Id: id,
                    Schema: SchemaNameOf(table),
                    Table: table.Name,
                    RowCount: rowCountById.TryGetValue(id, out var rows) ? rows : null,
                    ColumnCount: columns.Count,
                    IndexCount: indexes.Count,
                    ForeignKeyCount: table.ForeignKeys.Count,
                    Columns: columns,
                    PrimaryKey: table.PrimaryKey is null
                        ? null
                        : new RelationshipKeyView(table.PrimaryKey.Name, [.. table.PrimaryKey.Columns]),
                    UniqueKeys:
                    [
                        .. (table.UniqueConstraints ?? [])
                            .OrderBy(key => key.Name, StringComparer.Ordinal)
                            .Select(key => new RelationshipKeyView(key.Name, [.. key.Columns])),
                    ],
                    Indexes: indexes,
                    CheckConstraints:
                    [
                        .. (table.CheckConstraints ?? [])
                            .OrderBy(check => check.Name, StringComparer.Ordinal)
                            .Select(check => new RelationshipCheckConstraintView(
                                check.Name,
                                check.Expression?.Text ?? string.Empty)),
                    ]);
            })
            .ToList();

        var relatedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            relatedIds.Add(edge.ChildId);
            if (edge.ParentIsCaptured)
            {
                relatedIds.Add(edge.ParentId);
            }
        }

        var schemaViews = tables
            .GroupBy(SchemaNameOf, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var inSchema = g.ToList();
                return new RelationshipSchemaView(
                    Schema: g.Key,
                    TableCount: inSchema.Count,
                    RelatedTableCount: inSchema.Count(t => relatedIds.Contains(t.Ref.Value)));
            })
            .ToList();

        // A schema-only run states no row count for any table; that is the one flag the header needs.
        var hasData = tables.Any(t => rowCountById.TryGetValue(t.Ref.Value, out var rows) && rows is not null);

        return new RelationshipGraphView(
            Origin: new GraphViewOrigin(
                Kind: EGraphOriginKind.Catalog,
                Label: FolderName(catalogPath),
                Location: catalogPath,
                Dialect: manifest.SourceDialect,
                FormatVersion: manifest.FormatVersion),
            HasData: hasData,
            Tables: tableViews,
            OutsideTables: outsideById.Values.OrderBy(o => o.Id, StringComparer.Ordinal).ToList(),
            Schemas: schemaViews,
            Edges: edges
                .OrderBy(e => e.ChildId, StringComparer.Ordinal)
                .ThenBy(e => e.ConstraintName, StringComparer.Ordinal)
                .ThenBy(e => e.ParentId, StringComparer.Ordinal)
                .ToList(),
            UnrelatedTableCount: tables.Count(t => !relatedIds.Contains(t.Ref.Value)));
    }

    private static readonly (string? Schema, string? Name, bool StatesSomething) NoReference = (null, null, false);

    /// <summary>The folder's own name, never the path: the origin's display word for a catalog.</summary>
    private static string FolderName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    /// <summary>
    /// Reads what a reference states, without inventing anything for one that states nothing.
    /// </summary>
    private static (string? Schema, string? Name, bool StatesSomething) DescribeReference(ObjectRef reference)
    {
        if (string.IsNullOrEmpty(reference.Value))
        {
            return NoReference;
        }

        var schema = SafeSchemaName(reference);
        var name = SafeObjectName(reference);

        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(schema))
        {
            return NoReference;
        }

        return (string.IsNullOrEmpty(schema) ? null : schema, string.IsNullOrEmpty(name) ? null : name, true);
    }

    private static string? SafeSchemaName(ObjectRef reference)
    {
        if (string.IsNullOrEmpty(reference.Value))
        {
            return null;
        }

        try
        {
            return reference.SchemaName();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string SafeObjectName(ObjectRef reference)
    {
        if (string.IsNullOrEmpty(reference.Value))
        {
            return string.Empty;
        }

        try
        {
            return reference.ObjectName();
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// §5.1: an edge is mandatory when every referencing column is non-nullable.
    /// </summary>
    private static bool IsMandatory(
        IReadOnlyDictionary<string, ColumnDefinition> columnsByName,
        IReadOnlyList<string> referencingColumns)
    {
        if (referencingColumns.Count == 0)
        {
            return false;
        }

        foreach (var name in referencingColumns)
        {
            if (!columnsByName.TryGetValue(name, out var column) || column.Nullable)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// §5.2: <c>NO ACTION</c> is the default the caller never needs to see, so it reads as
    /// "not stated" just as an absent action does.
    /// </summary>
    private static string? NormalizeAction(string? action) =>
        string.IsNullOrWhiteSpace(action)
        || string.Equals(action, "NO ACTION", StringComparison.OrdinalIgnoreCase)
            ? null
            : action;

    private static HashSet<string> GetOrAdd(Dictionary<string, HashSet<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            map[key] = set;
        }

        return set;
    }

    /// <inheritdoc />
    public async Task<VerificationSummary> VerifyAsync(
        string catalogPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);

        _logger.LogInformation("Verify started: catalog={CatalogPath}", catalogPath);

        var verifier = new CatalogVerifier();
        var result = await verifier.VerifyAsync(catalogPath, cancellationToken);

        return new VerificationSummary(
            Passed: result.IsValid,
            Issues: result.Issues.Select(issue =>
                new InterchangeIssueView(
                    EInterchangeSeverity.Error,
                    "VerificationFailed",
                    issue,
                    string.Empty,
                    string.Empty)).ToList());
    }

    /// <inheritdoc />
    public async Task<BlobExtractionResult> ExtractBlobsAsync(
        BlobExtractionRequest request,
        IProgress<BlobExtractionProgressView>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogInformation(
            "Blob extraction started: catalog={CatalogPath} schema={Schema} table={Table} output={OutputDir}",
            request.CatalogPath,
            request.Schema,
            request.Table,
            request.OutputDirectory);

        var productRequest = new DatabaseInterchange.Transform.BlobExtractionRequest(
            request.CatalogPath,
            request.Schema,
            request.Table,
            request.OutputDirectory);

        IProgress<DatabaseInterchange.Transform.BlobExtractionProgress>? progressAdapter = null;
        if (progress is not null)
        {
            progressAdapter = new Progress<DatabaseInterchange.Transform.BlobExtractionProgress>(p =>
                progress.Report(new BlobExtractionProgressView(
                    p.Stage,
                    p.RowsProcessed,
                    p.TotalRows)));
        }

        var extractor = new DatabaseInterchange.Transform.BlobExtractor(_logger);
        var result = await extractor.ExtractAsync(productRequest, progressAdapter, cancellationToken).ConfigureAwait(false);

        return new BlobExtractionResult(
            result.FilesExtracted,
            result.BytesExtracted,
            result.RowsProcessed,
            result.Elapsed);
    }

    /// <inheritdoc />
    public async Task<BlobExtractionResult> ExtractAllBlobsAsync(
        string catalogPath,
        string outputDirectory,
        IProgress<BlobExtractionProgressView>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        _logger.LogInformation(
            "Extract all blobs started: catalog={CatalogPath} output={OutputDir}",
            catalogPath,
            outputDirectory);

        IProgress<DatabaseInterchange.Transform.BlobExtractionProgress>? progressAdapter = null;
        if (progress is not null)
        {
            progressAdapter = new Progress<DatabaseInterchange.Transform.BlobExtractionProgress>(p =>
                progress.Report(new BlobExtractionProgressView(
                    p.Stage,
                    p.RowsProcessed,
                    p.TotalRows)));
        }

        var extractor = new DatabaseInterchange.Transform.BlobExtractor(_logger);
        var result = await extractor.ExtractAllAsync(catalogPath, outputDirectory, progressAdapter, cancellationToken).ConfigureAwait(false);

        return new BlobExtractionResult(
            result.FilesExtracted,
            result.BytesExtracted,
            result.RowsProcessed,
            result.Elapsed);
    }

    /// <summary>
    /// Issue code an adapter raises for one table whose recorded row count differs from the count the
    /// target holds after the restore. The validation report carries it per table, so the summary count
    /// the application reports is read from it rather than counted twice.
    /// </summary>
    private const string RowCountMismatchCode = "RowCountMismatch";

    /// <summary>
    /// Turns the product's import outcome into the result the application reports. The validation report
    /// is part of the answer rather than a footnote: its findings join the issues the import raised, so a
    /// restore never hides what it checked and did not carry, and a validation that did not pass keeps the
    /// result out of the plain-success branch even when every stage reported no problem.
    /// </summary>
    internal static InterchangeRestoreResult BuildRestoreResult(
        DatabaseInterchange.Transform.ImportResult result,
        TimeSpan elapsed)
    {
        var report = result.ValidationReport;
        var issues = result.Issues.Select(MapIssue).ToList();
        if (report is not null)
            issues.AddRange(report.Issues.Select(MapIssue));

        var outcome = result.Success switch
        {
            false => ERestoreOutcome.Failed,
            true when issues.Count > 0 || report is { Passed: false } => ERestoreOutcome.CompletedWithIssues,
            _ => ERestoreOutcome.Success,
        };

        return new InterchangeRestoreResult(
            Outcome: outcome,
            ObjectsApplied: result.ObjectsApplied,
            ObjectsSkipped: result.ObjectsSkipped,
            RowsRestored: result.RowsImported,
            RowCountMismatches: report is null
                ? 0
                : report.Issues.Count(i => i.Code == RowCountMismatchCode),
            Issues: issues,
            Validation: report is null
                ? null
                : new VerificationSummary(Passed: report.Passed, Issues: report.Issues.Select(MapIssue).ToList()),
            LastCompletedChunk: null, // TODO: Track when importer provides this
            Elapsed: elapsed);
    }

    /// <summary>
    /// Turns a path the product named relative to the catalog it was handed into the path the application
    /// reports, or <see langword="null"/> when the product wrote no such file.
    /// </summary>
    private static string? ToAbsolute(string catalogDirectory, string? relativePath) =>
        relativePath is null ? null : Path.Combine(catalogDirectory, relativePath);

    private static InterchangeIssueView MapIssue(InterchangeIssue issue) =>
        new(
            MapSeverity(issue.Severity),
            issue.Code,
            issue.Message,
            string.Empty,
            string.Empty);

    private static EInterchangeSeverity MapSeverity(EIssueSeverity severity) => severity switch
    {
        EIssueSeverity.Info => EInterchangeSeverity.Information,
        EIssueSeverity.Warning => EInterchangeSeverity.Warning,
        EIssueSeverity.Error => EInterchangeSeverity.Error,
        _ => EInterchangeSeverity.Information,
    };
}
