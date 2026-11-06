using DbAdapter.Definitions;
using DbClone.Application.Compare;
using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.TableFilter;
using DbClone.Application.Targets;

using Microsoft.Extensions.Logging;
using CanonicalModel.Models;
using CanonicalModel.Enums;
using CanonicalModel.DTOs;
using CanonicalModel.Exceptions;

namespace DbClone.Application.Comparison;

/// <summary>
/// Compares two databases and reports the differences. Replaces the UI-layer
/// <c>WorkflowState</c> with a plain <see cref="Action{T1,T2}"/> so the service
/// can live in the application layer without depending on UI types.
/// </summary>
public sealed class ComparisonService : IComparisonService
{
    private readonly IDatabaseAdapter _adapter;

    private readonly DatabaseTargetFactory _targetFactory;

    private readonly ITableFilterApplier _filterApplier;

    private readonly ILogger<ComparisonService> _logger;

    private readonly IEnumerable<IModelComparer> _modelComparers;

    /// <summary>Initializes a new instance.</summary>
    public ComparisonService(
        IDatabaseAdapter adapter,
        DatabaseTargetFactory targetFactory,
        IEnumerable<IModelComparer> modelComparers,
        ITableFilterApplier filterApplier,
        ILogger<ComparisonService> logger)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _targetFactory = targetFactory ?? throw new ArgumentNullException(nameof(targetFactory));
        _modelComparers = modelComparers ?? throw new ArgumentNullException(nameof(modelComparers));
        _filterApplier = filterApplier ?? throw new ArgumentNullException(nameof(filterApplier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<CompareDatabasesResult> CompareDatabasesAsync(
        ConnectionInfo source,
        ConnectionInfo destination,
        Action<ELogLevel, string> log,
        EVerifyMode mode,
        bool excludePlatformSchemas,
        IProgress<CompareProgressInfo>? progress,
        Func<CancellationToken, Task>? waitWhilePaused,
        CancellationToken ct,
        TableSelectionSpec? tableSelection = null)
    {
        var sourceTarget = _targetFactory.Create(source);
        var destTarget = _targetFactory.Create(destination);
        var metadata = _adapter.CreateMetadataSource(sourceTarget);
        var comparer = _adapter.CreateComparer();

        // ─── Log effective compare settings ───
        log(ELogLevel.Info,
            $"Compare options: VerifyMode={mode}, PlatformSchemas={(excludePlatformSchemas ? "Excluded" : "Included")}");

        // ─── Read full database models from both sides ───
        progress?.Report(new CompareProgressInfo
        {
            PercentComplete = 0,
            CurrentPhase = "Reading source model",
            CurrentTable = "",
            TablesProcessed = 0,
            TotalTables = 0
        });

        log(ELogLevel.Info,
            $"Reading database model from source ({source.Host}:{source.Port}/{source.DatabaseName})...");
        var sourceModel = await metadata.ReadDatabaseModelAsync(
                              excludePlatformSchemas,
                              ct);
        log(ELogLevel.Info, $"Source: {ModelSummary(sourceModel)}");

        progress?.Report(new CompareProgressInfo
        {
            PercentComplete = 5,
            CurrentPhase = "Reading destination model",
            CurrentTable = "",
            TablesProcessed = 0,
            TotalTables = 0
        });

        var destMetadata = _adapter.CreateMetadataSource(destTarget);
        log(ELogLevel.Info,
            $"Reading database model from destination ({destination.Host}:{destination.Port}/{destination.DatabaseName})...");

        var destModel = await destMetadata.ReadDatabaseModelAsync(
                            excludePlatformSchemas: false,
                            ct: ct);

        if (excludePlatformSchemas)
        {
            var schemasWithObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in sourceModel.Tables) schemasWithObjects.Add(t.SchemaName);
            foreach (var v in sourceModel.Views) schemasWithObjects.Add(v.SchemaName);
            foreach (var v in sourceModel.MaterializedViews) schemasWithObjects.Add(v.SchemaName);
            foreach (var f in sourceModel.Functions) schemasWithObjects.Add(f.SchemaName);
            foreach (var s in sourceModel.Sequences) schemasWithObjects.Add(s.SchemaName);
            foreach (var e in sourceModel.Enums) schemasWithObjects.Add(e.SchemaName);
            foreach (var d in sourceModel.Domains) schemasWithObjects.Add(d.SchemaName);
            foreach (var c in sourceModel.CompositeTypes) schemasWithObjects.Add(c.SchemaName);

            var sourceUserSchemas = sourceModel.Schemas
                .Where(s => !s.IsSystem && schemasWithObjects.Contains(s.Name))
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            sourceModel = sourceModel.FilterToSchemas(sourceUserSchemas);
            destModel = destModel.FilterToSchemas(sourceUserSchemas);
            log(ELogLevel.Info, $"Destination (filtered to source schemas): {ModelSummary(destModel)}");
        }
        else
        {
            log(ELogLevel.Info, $"Destination: {ModelSummary(destModel)}");
        }

        progress?.Report(new CompareProgressInfo
        {
            PercentComplete = 10,
            CurrentPhase = "Comparing tables",
            CurrentTable = "",
            TablesProcessed = 0,
            TotalTables = 0
        });

        if (destModel.Tables.Count == 0)
            log(ELogLevel.Warning,
                "Destination database has no tables. Verify the connection points to the correct database.");

        log(ELogLevel.Hint,
            "System-managed schemas are not compared.");
        if (excludePlatformSchemas)
            log(ELogLevel.Hint,
                "Platform and system schemas excluded. Comparison restricted to user schemas in source.");
        else
            log(ELogLevel.Hint,
                "Platform schemas are included. Uncheck 'Platform Schemas' in Compare Options to exclude them.");

        var allItems = new List<CompareResultItem>();

        // ─── 0. Apply the active table selection scope ───────────────────────
        if (tableSelection is { IsActive: true })
        {
            (sourceModel, destModel) = ApplyTableSelectionScope(
                sourceModel,
                destModel,
                tableSelection,
                allItems,
                log);
        }

        // ─── 1. Compare tables (presence + row counts / checksums) ───
        await CompareTableDataAsync(
            log, sourceModel, destModel, source, destination,
            sourceTarget, destTarget, comparer,
            mode, allItems, progress, waitWhilePaused, ct);

        // ─── 2. Compare schema objects via IModelComparer implementations ───
        log(ELogLevel.Info, "Comparing schema objects (schemas, indexes, views, functions, sequences, triggers, types, table DDL)...");
        await RunModelComparersAsync(sourceModel, destModel, allItems, progress, waitWhilePaused, ct);

        progress?.Report(new CompareProgressInfo
        {
            PercentComplete = 100,
            CurrentPhase = "Complete",
            CurrentTable = "",
            TablesProcessed = 0,
            TotalTables = 0
        });

        var sorted = allItems
            .OrderBy(i => i.ObjectType)
            .ThenBy(i => i.Status switch
                {
                    ECompareStatus.Identical => 2,
                    ECompareStatus.Notice => 1,
                    _ => 0
                })
            .ThenBy(i => i.TableName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LogObjectSummary(sorted, log);

        return new CompareDatabasesResult(
            Items: sorted,
            TotalIdentical: sorted.Count(i => i.Status == ECompareStatus.Identical),
            TotalNotices: sorted.Count(i => i.Status == ECompareStatus.Notice),
            TotalDifferent: sorted.Count(i => i.Status == ECompareStatus.Different),
            TotalMissingSource: sorted.Count(i => i.Status == ECompareStatus.MissingSource),
            TotalMissingDest: sorted.Count(i => i.Status == ECompareStatus.MissingDest),
            TotalSkipped: sorted.Count(i => i.Status == ECompareStatus.Skipped),
            TotalErrors: sorted.Count(i => i.Status == ECompareStatus.Error));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Table selection scope
    // ─────────────────────────────────────────────────────────────────────────

    private (DatabaseModel Source, DatabaseModel Destination) ApplyTableSelectionScope(
        DatabaseModel source,
        DatabaseModel destination,
        TableSelectionSpec spec,
        List<CompareResultItem> items,
        Action<ELogLevel, string> log)
    {
        var sourceResult = _filterApplier.Apply(source, spec);

        var selectedTables = sourceResult.FilteredModel.Tables
            .Select(t => new TableId(t.SchemaName, t.Name))
            .ToHashSet();
        var destExclusions = destination.Tables
            .Select(t => new TableId(t.SchemaName, t.Name))
            .Where(id => !selectedTables.Contains(id))
            .ToHashSet();
        var destResult = _filterApplier.Apply(destination, new TableSelectionSpec(true, destExclusions));

        log(ELogLevel.Info,
            $"Table selection active: {sourceResult.Report.RemovedTables.Count} tables excluded, {sourceResult.FilteredModel.Tables.Count} tables in scope.");

        if (sourceResult.Report.StaleExclusions.Count > 0)
            log(ELogLevel.Warning,
                $"{sourceResult.Report.StaleExclusions.Count} selected exclusion(s) matched no source table and were ignored.");

        var report = sourceResult.Report;

        foreach (var view in report.SkippedViews)
        {
            items.Add(new CompareResultItem
                {
                    ObjectType = EDatabaseObjectType.View,
                    SchemaName = view.Schema,
                    TableName = view.FullName,
                    Status = ECompareStatus.Skipped,
                    SkipReason = ESkipReason.TableSelection,
                    SourceCount = -1,
                    DestCount = -1,
                    Details = "View depends on a table excluded by the active table selection"
                });
        }

        foreach (var partition in report.OrphanedPartitions)
        {
            items.Add(new CompareResultItem
                {
                    ObjectType = EDatabaseObjectType.Table,
                    SchemaName = partition.Schema,
                    TableName = partition.FullName,
                    Status = ECompareStatus.Skipped,
                    SkipReason = ESkipReason.TableSelection,
                    SourceCount = -1,
                    DestCount = -1,
                    Details = "Partition of a table excluded by the active table selection"
                });
        }

        foreach (var fk in report.DroppedForeignKeys)
        {
            items.Add(new CompareResultItem
                {
                    ObjectType = EDatabaseObjectType.Constraint,
                    SchemaName = fk.OwningTable.Schema,
                    TableName = fk.OwningTable.FullName,
                    Status = ECompareStatus.Notice,
                    SourceCount = -1,
                    DestCount = -1,
                    Details =
                        $"Foreign key {fk.ConstraintName} → {fk.ReferencedTable.FullName} ignored (referenced table excluded by the active table selection)"
                });
        }

        return (sourceResult.FilteredModel, destResult.FilteredModel);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Model comparers delegation
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunModelComparersAsync(
        DatabaseModel source,
        DatabaseModel dest,
        List<CompareResultItem> allItems,
        IProgress<CompareProgressInfo>? progress,
        Func<CancellationToken, Task>? waitWhilePaused,
        CancellationToken ct)
    {
        var comparerList = _modelComparers.ToList();
        var totalComparers = comparerList.Count;
        var processed = 0;

        foreach (var comparer in comparerList)
        {
            ct.ThrowIfCancellationRequested();
            if (waitWhilePaused is not null)
                await waitWhilePaused(ct);

            try
            {
                var results = comparer.Compare(source, dest, ct);
                foreach (var item in results)
                {
                    allItems.Add(new CompareResultItem
                        {
                            ObjectType = item.ObjectType,
                            SchemaName = item.SchemaName,
                            TableName = item.ObjectName,
                            Status = item.Status,
                            SourceCount = -1,
                            DestCount = -1,
                            Details = item.Details
                        });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Model comparer {Comparer} failed", comparer.GetType().Name);
            }

            processed++;
            var percent = totalComparers > 0
                ? 90 + (int)(processed * 10.0 / totalComparers)
                : 100;
            progress?.Report(new CompareProgressInfo
            {
                PercentComplete = percent,
                CurrentPhase = "Comparing schema objects",
                CurrentTable = comparer.GetType().Name.Replace("Comparer", ""),
                TablesProcessed = processed,
                TotalTables = totalComparers
            });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Table data comparison
    // ─────────────────────────────────────────────────────────────────────────

    private async Task CompareTableDataAsync(
        Action<ELogLevel, string> log,
        DatabaseModel sourceModel,
        DatabaseModel destModel,
        ConnectionInfo sourceInfo,
        ConnectionInfo destInfo,
        DatabaseTarget sourceTarget,
        DatabaseTarget destTarget,
        ITableComparer comparer,
        EVerifyMode mode,
        List<CompareResultItem> allItems,
        IProgress<CompareProgressInfo>? progress,
        Func<CancellationToken, Task>? waitWhilePaused,
        CancellationToken ct)
    {
        var sourceTables = sourceModel.Tables
            .Select(t => (Schema: t.SchemaName, t.Name)).ToList();
        var destTables = destModel.Tables
            .Select(t => (Schema: t.SchemaName, t.Name)).ToList();
        LogSchemaBreakdown(sourceTables, ECompareSide.Source.ToDisplayText(), log);
        LogSchemaBreakdown(destTables, ECompareSide.Destination.ToDisplayText(), log);

        var sourceTableDict = sourceTables.ToDictionary(t => $"{t.Schema}.{t.Name}", t => t);
        var destTableDict = destTables.ToDictionary(t => $"{t.Schema}.{t.Name}", t => t);

        var onlyInSource = sourceTables
            .Where(t => !destTableDict.ContainsKey($"{t.Schema}.{t.Name}")).ToList();
        var onlyInDest = destTables.Where(t => !sourceTableDict.ContainsKey($"{t.Schema}.{t.Name}"))
            .ToList();
        var commonTables = sourceTables
            .Where(t => destTableDict.ContainsKey($"{t.Schema}.{t.Name}")).ToList();

        log(ELogLevel.Info,
            $"Tables: {commonTables.Count} in both, {onlyInSource.Count} missing in dest, {onlyInDest.Count} missing in source");

        if (commonTables.Count == 0 && sourceTables.Count > 0 && destTables.Count > 0)
        {
            log(ELogLevel.Warning, "The two databases share NO common tables.");
            log(ELogLevel.Warning, $"   Source has e.g.: {SampleNames(sourceTables)}");
            log(ELogLevel.Warning, $"   Destination has e.g.: {SampleNames(destTables)}");
            log(ELogLevel.Warning,
                "   This usually means the source and destination connections are swapped, or one points at the wrong database. Please double-check both connections.");
        }
        else if (commonTables.Count > 0
                 && onlyInSource.Count + onlyInDest.Count > commonTables.Count)
        {
            log(ELogLevel.Warning,
                $"More tables differ ({onlyInSource.Count + onlyInDest.Count}) than match ({commonTables.Count}). If this is unexpected, verify both connections point at the intended databases.");
        }

        var deniedSchemas = new Dictionary<string, ECompareSide>(StringComparer.OrdinalIgnoreCase);
        var totalItems = onlyInSource.Count + onlyInDest.Count + commonTables.Count;
        var processedItems = 0;

        foreach (var (schema, name) in onlyInSource)
        {
            ct.ThrowIfCancellationRequested();
            if (waitWhilePaused is not null)
                await waitWhilePaused(ct);

            var qualifiedName = $"{schema}.{name}";
            long sourceCount = -1;
            string? sourceCountError = null;

            if (deniedSchemas.ContainsKey(schema))
            {
                sourceCountError = $"permission denied for schema {schema}";
            }
            else
            {
                try
                {
                    sourceCount = await comparer.CountRowsAsync(
                                      sourceTarget,
                                      schema,
                                      name,
                                      ct);
                }
                catch (Exception ex) when (IsPermissionDenied(ex))
                {
                    deniedSchemas[schema] = ECompareSide.Source;
                    sourceCountError = ex.Message;
                    _logger.LogWarning(
                        ex,
                        "Permission denied for schema {Schema}, skipping remaining tables",
                        schema);
                    log(ELogLevel.Warning,
                        $"Permission denied for schema {schema} — skipping remaining tables in this schema.");
                }
                catch (Exception ex)
                {
                    sourceCountError = ex.Message;
                    _logger.LogWarning(
                        ex,
                        "Failed to count rows for {Table} in source",
                        qualifiedName);
                    log(ELogLevel.Warning, $"Could not count rows for {qualifiedName}: {ex.Message}");
                }
            }

            allItems.Add(
                new CompareResultItem
                    {
                        ObjectType = EDatabaseObjectType.Table,
                        SchemaName = schema,
                        TableName = qualifiedName,
                        Status = ECompareStatus.MissingDest,
                        SourceCount = sourceCount,
                        DestCount = 0,
                        Details = sourceCount >= 0
                                      ? $"Table exists only in source ({sourceCount:N0} rows)"
                                      : sourceCountError != null
                                          ? $"Table exists only in source (row count failed: {sourceCountError})"
                                          : "Table exists only in source"
                    });

            processedItems++;
            ReportTableProgress(progress, processedItems, totalItems, qualifiedName);
        }

        foreach (var (schema, name) in onlyInDest)
        {
            ct.ThrowIfCancellationRequested();
            if (waitWhilePaused is not null)
                await waitWhilePaused(ct);

            var qualifiedName = $"{schema}.{name}";
            long destCount = -1;
            string? destCountError = null;

            if (deniedSchemas.ContainsKey(schema))
            {
                destCountError = $"permission denied for schema {schema}";
            }
            else
            {
                try
                {
                    destCount = await comparer.CountRowsAsync(
                                    destTarget,
                                    schema,
                                    name,
                                    ct);
                }
                catch (Exception ex) when (IsPermissionDenied(ex))
                {
                    deniedSchemas[schema] = ECompareSide.Destination;
                    destCountError = ex.Message;
                    _logger.LogWarning(
                        ex,
                        "Permission denied for schema {Schema}, skipping remaining tables",
                        schema);
                    log(ELogLevel.Warning,
                        $"Permission denied for schema {schema} — skipping remaining tables in this schema.");
                }
                catch (Exception ex)
                {
                    destCountError = ex.Message;
                    _logger.LogWarning(
                        ex,
                        "Failed to count rows for {Table} in destination",
                        qualifiedName);
                    log(ELogLevel.Warning, $"Could not count rows for {qualifiedName}: {ex.Message}");
                }
            }

            allItems.Add(
                new CompareResultItem
                    {
                        ObjectType = EDatabaseObjectType.Table,
                        SchemaName = schema,
                        TableName = qualifiedName,
                        Status = ECompareStatus.MissingSource,
                        SourceCount = 0,
                        DestCount = destCount,
                        Details = destCount >= 0
                                      ? $"Table exists only in destination ({destCount:N0} rows)"
                                      : destCountError != null
                                          ? $"Table exists only in destination (row count failed: {destCountError})"
                                          : "Table exists only in destination"
                    });

            processedItems++;
            ReportTableProgress(progress, processedItems, totalItems, qualifiedName);
        }

        var skippedBySchema = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (schema, name) in commonTables)
        {
            ct.ThrowIfCancellationRequested();
            if (waitWhilePaused is not null)
                await waitWhilePaused(ct);

            var qualifiedName = $"{schema}.{name}";

            if (deniedSchemas.TryGetValue(schema, out var deniedSide))
            {
                skippedBySchema.TryGetValue(schema, out var count);
                skippedBySchema[schema] = count + 1;
                allItems.Add(
                    new CompareResultItem
                        {
                            ObjectType = EDatabaseObjectType.Table,
                            SchemaName = schema,
                            TableName = qualifiedName,
                            Status = ECompareStatus.Skipped,
                            SkipReason = ESkipReason.PermissionDenied,
                            SkipSide = deniedSide,
                            SourceCount = -1,
                            DestCount = -1,
                            Details =
                                $"Table exists in both — cannot compare ({FormatSide(deniedSide)}: permission denied for schema {schema})"
                        });
                processedItems++;
                ReportTableProgress(progress, processedItems, totalItems, qualifiedName);
                continue;
            }

            log(ELogLevel.Info, $"Comparing {qualifiedName} ({processedItems + 1}/{totalItems})...");

            try
            {
                var result = await comparer.CompareTableAsync(
                                 sourceTarget,
                                 destTarget,
                                 schema,
                                 name,
                                 mode,
                                 ct);
                allItems.Add(
                    new CompareResultItem
                        {
                            ObjectType = EDatabaseObjectType.Table,
                            SchemaName = schema,
                            TableName = qualifiedName,
                            Status =
                                result.IsMatch
                                    ? ECompareStatus.Identical
                                    : ECompareStatus.Different,
                            SourceCount = result.SourceCount,
                            DestCount = result.DestCount,
                            RowsAdded = result.RowsAdded,
                            RowsRemoved = result.RowsRemoved,
                            RowsModified = result.RowsModified,
                            Details = result.IsMatch ? "" : GetDifferenceDescription(result, mode)
                        });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsPermissionDenied(ex))
            {
                var side = ex is TableCompareException tce ? tce.Side : ECompareSide.Source;
                deniedSchemas[schema] = side;
                skippedBySchema[schema] = 1;
                _logger.LogWarning(
                    ex,
                    "Permission denied for schema {Schema}, skipping remaining tables",
                    schema);
                log(ELogLevel.Warning,
                    $"Permission denied for schema {schema} — skipping remaining tables in this schema.");
                allItems.Add(
                    new CompareResultItem
                        {
                            ObjectType = EDatabaseObjectType.Table,
                            SchemaName = schema,
                            TableName = qualifiedName,
                            Status = ECompareStatus.Skipped,
                            SkipReason = ESkipReason.PermissionDenied,
                            SkipSide = side,
                            SourceCount = -1,
                            DestCount = -1,
                            Details =
                                $"Table exists in both — cannot compare ({DescribeFailure(ex)})"
                        });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compare {Table}", qualifiedName);
                log(ELogLevel.Error, $"Could not compare {qualifiedName}: {ex.Message}");
                allItems.Add(
                    new CompareResultItem
                        {
                            ObjectType = EDatabaseObjectType.Table,
                            SchemaName = schema,
                            TableName = qualifiedName,
                            Status = ECompareStatus.Error,
                            SourceCount = -1,
                            DestCount = -1,
                            Details = DescribeFailure(ex)
                        });
            }

            processedItems++;
            ReportTableProgress(progress, processedItems, totalItems, qualifiedName);
        }

        if (skippedBySchema.Count > 0)
        {
            var summary = string.Join(
                ", ",
                skippedBySchema.Select(kv => $"{kv.Key} ({kv.Value} tables)"));
            log(ELogLevel.Warning, $"Skipped due to insufficient permissions: {summary}");
        }
    }

    /// <summary>Reports table comparison progress using the phase-based 10–90% allocation.</summary>
    private static void ReportTableProgress(
        IProgress<CompareProgressInfo>? progress,
        int processedItems,
        int totalItems,
        string currentTable)
    {
        if (progress is null) return;

        var percent = totalItems > 0
            ? 10 + (int)(processedItems * 80.0 / totalItems)
            : 90;

        progress.Report(new CompareProgressInfo
        {
            PercentComplete = percent,
            CurrentPhase = "Comparing tables",
            CurrentTable = currentTable,
            TablesProcessed = processedItems,
            TotalTables = totalItems
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Summary logging
    // ─────────────────────────────────────────────────────────────────────────

    private static void LogObjectSummary(
        List<CompareResultItem> sorted,
        Action<ELogLevel, string> log)
    {
        foreach (var group in sorted.GroupBy(i => i.ObjectType).OrderBy(g => g.Key))
        {
            var label = group.Key switch
                {
                    EDatabaseObjectType.Table => "Tables",
                    EDatabaseObjectType.Schema => "Schemas",
                    EDatabaseObjectType.Index => "Indexes",
                    EDatabaseObjectType.View => "Views",
                    EDatabaseObjectType.MaterializedView => "Materialized views",
                    EDatabaseObjectType.Function => "Functions",
                    EDatabaseObjectType.Sequence => "Sequences",
                    EDatabaseObjectType.Trigger => "Triggers",
                    EDatabaseObjectType.Enum => "Enums",
                    EDatabaseObjectType.Domain => "Domains",
                    EDatabaseObjectType.CompositeType => "Composite types",
                    _ => group.Key.ToString()
                };
            var identical = group.Count(i => i.Status == ECompareStatus.Identical);
            var notices = group.Count(i => i.Status == ECompareStatus.Notice);
            var different = group.Count(i => i.Status == ECompareStatus.Different);
            var missingDest = group.Count(i => i.Status == ECompareStatus.MissingDest);
            var missingSrc = group.Count(i => i.Status == ECompareStatus.MissingSource);
            var missingPart = (missingDest, missingSrc) switch
                {
                    (0, 0) => "0 missing",
                    (var d, 0) => $"{d} missing in dest",
                    (0, var s) => $"{s} missing in source",
                    (var d, var s) => $"{d} missing in dest, {s} missing in source"
                };
            var hasMissing = missingDest > 0 || missingSrc > 0;
            var hasDiff = different > 0;
            var noticePart = notices > 0 ? $", {notices} notices" : "";
            var summary = $"  {label}: {identical} identical{noticePart}, {different} different, {missingPart}";
            if (hasMissing || hasDiff)
                log(ELogLevel.Error, summary);
            else
                log(ELogLevel.Info, summary);

            if (missingDest > 0)
            {
                foreach (var item in group.Where(i => i.Status == ECompareStatus.MissingDest))
                    log(ELogLevel.Error, $"  {item.TableName}: missing in destination");
            }

            if (missingSrc > 0)
            {
                foreach (var item in group.Where(i => i.Status == ECompareStatus.MissingSource))
                    log(ELogLevel.Error, $"  {item.TableName}: missing in source");
            }

            if (different > 0)
            {
                foreach (var item in group.Where(i => i.Status == ECompareStatus.Different))
                    log(ELogLevel.Error, $"  {item.TableName}: {item.Details}");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string DescribeFailure(Exception ex)
    {
        if (ex is TableCompareException tce)
            return $"{FormatSide(tce.Side)}: {tce.Message}";
        return ex.Message;
    }

    private static string GetDifferenceDescription(TableCompareResult result, EVerifyMode mode)
    {
        if (result.SourceCount != result.DestCount)
            return $"Row count differs (source: {result.SourceCount}, dest: {result.DestCount})";

        if (result.RowsAdded > 0 || result.RowsRemoved > 0)
        {
            var parts = new List<string>();
            if (result.RowsAdded > 0) parts.Add($"{result.RowsAdded} added");
            if (result.RowsRemoved > 0) parts.Add($"{result.RowsRemoved} removed");
            if (result.RowsModified > 0) parts.Add($"{result.RowsModified} modified");
            return "Rows " + string.Join(", ", parts);
        }

        if (result.RowsModified > 0)
            return $"{result.RowsModified} rows modified";

        return mode == EVerifyMode.RowCount
                   ? "Row count matches (data may differ)"
                   : "Data content differs";
    }

    private static bool IsPermissionDenied(Exception ex)
    {
        if (ex is TableCompareException { SqlState: not null } tce)
            return tce.SqlState == "42501";

        const string indicator = "permission denied";
        return ex.Message.Contains(indicator, StringComparison.OrdinalIgnoreCase)
               || (ex.InnerException?.Message.Contains(
                       indicator,
                       StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static void LogSchemaBreakdown(
        List<(string Schema, string Name)> tables,
        string side,
        Action<ELogLevel, string> log)
    {
        if (tables.Count == 0)
            return;

        var breakdown = tables
            .GroupBy(t => t.Schema)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key}: {g.Count()}");

        log(ELogLevel.Info, $"{side} schemas: {string.Join(", ", breakdown)}");
    }

    private static string SampleNames(List<(string Schema, string Name)> tables)
    {
        var sample = tables
            .OrderBy(t => t.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(t => $"{t.Schema}.{t.Name}");

        return string.Join(", ", sample);
    }

    private static string FormatSide(ECompareSide side) => side.ToDisplayText();

    private static string ModelSummary(DatabaseModel model)
    {
        var parts = new List<string>(8);
        if (model.Tables.Count > 0) parts.Add($"{model.Tables.Count} tables");
        if (model.Views.Count > 0) parts.Add($"{model.Views.Count} views");
        if (model.MaterializedViews.Count > 0)
            parts.Add($"{model.MaterializedViews.Count} materialized views");
        if (model.Sequences.Count > 0) parts.Add($"{model.Sequences.Count} sequences");
        if (model.Functions.Count > 0) parts.Add($"{model.Functions.Count} functions");
        if (model.Triggers.Count > 0) parts.Add($"{model.Triggers.Count} triggers");
        if (model.Enums.Count > 0) parts.Add($"{model.Enums.Count} enums");
        if (model.Domains.Count > 0) parts.Add($"{model.Domains.Count} domains");
        if (model.CompositeTypes.Count > 0)
            parts.Add($"{model.CompositeTypes.Count} composite types");
        return parts.Count > 0 ? string.Join(", ", parts) : "no objects";
    }
}
