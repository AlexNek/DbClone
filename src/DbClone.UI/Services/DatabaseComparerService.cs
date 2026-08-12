using DbClone.Application.Compare;
using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Exceptions;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.Application.TableFilter;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using Microsoft.Extensions.Logging;

namespace DbClone.UI.Services;

public sealed class DatabaseComparerService : IDatabaseComparerService
{
    private readonly ITableFilterApplier _filterApplier;

    private readonly ILogger<DatabaseComparerService> _logger;

    private readonly IEnumerable<IModelComparer> _modelComparers;

    private readonly ITableComparerProvider _tableComparerProvider;

    private readonly ITableInfoProvider _tableInfoProvider;

    public DatabaseComparerService(
        ITableInfoProvider tableInfoProvider,
        ITableComparerProvider tableComparerProvider,
        IEnumerable<IModelComparer> modelComparers,
        ITableFilterApplier filterApplier,
        ILogger<DatabaseComparerService> logger)
    {
        _tableInfoProvider = tableInfoProvider;
        _tableComparerProvider = tableComparerProvider;
        _modelComparers = modelComparers;
        _filterApplier = filterApplier;
        _logger = logger;
    }

    public async Task<CompareDatabasesResult> CompareDatabasesAsync(
        ConnectionViewModel source,
        ConnectionViewModel destination,
        WorkflowState state,
        EVerifyMode mode,
        bool excludePlatformSchemas,
        IProgress<CompareProgressInfo>? progress,
        Func<CancellationToken, Task>? waitWhilePaused,
        CancellationToken ct,
        TableSelectionSpec? tableSelection = null)
    {
        var sourceInfo = ConnectionInfoFactory.FromViewModel(source);
        var destInfo = ConnectionInfoFactory.FromViewModel(destination);

        // ─── Log effective compare settings ───
        state.Log(
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

        state.Log(
            $"Reading database model from source ({sourceInfo.Host}:{sourceInfo.Port}/{sourceInfo.DatabaseName})...");
        var sourceModel = await _tableInfoProvider.ReadDatabaseModelAsync(
                              sourceInfo,
                              excludePlatformSchemas,
                              ct);
        state.Log($"Source: {ModelSummary(sourceModel)}");

        progress?.Report(new CompareProgressInfo
        {
            PercentComplete = 5,
            CurrentPhase = "Reading destination model",
            CurrentTable = "",
            TablesProcessed = 0,
            TotalTables = 0
        });

        state.Log(
            $"Reading database model from destination ({destInfo.Host}:{destInfo.Port}/{destInfo.DatabaseName})...");

        // When excluding platform schemas, read the destination unfiltered, then
        // restrict it to the source's schema set. Rationale:
        //   - The source's platform-detection heuristic (role-ownership probing)
        //     works reliably on hosted platforms (Supabase, Aiven, Neon, etc.)
        //     where platform service roles are distinguishable.
        //   - On a local clone/restore, the same platform schemas (auth, storage,
        //     realtime, etc.) are owned by the connecting user, making the heuristic
        //     ineffective. Reading unfiltered then applying the source's schema set
        //     ensures both sides are compared consistently.
        //   - This follows SRP: the metadata reader reads; the compare service
        //     decides which schemas are relevant for comparison.
        var destModel = await _tableInfoProvider.ReadDatabaseModelAsync(
                            destInfo,
                            excludePlatformSchemas: false,
                            ct);

        if (excludePlatformSchemas)
        {
            // Only user schemas from the source that actually contain objects are
            // relevant. System schemas (pg_catalog, information_schema, pg_toast)
            // and empty platform-convention schemas (e.g. Supabase's "extensions")
            // are excluded — they add noise without value.
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
            state.Log($"Destination (filtered to source schemas): {ModelSummary(destModel)}");
        }
        else
        {
            state.Log($"Destination: {ModelSummary(destModel)}");
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
            state.LogWarning(
                "Destination database has no tables. Verify the connection points to the correct database.");

        state.LogHint(
            "pg_catalog, information_schema, pg_toast: managed by PostgreSQL — not compared.");
        if (excludePlatformSchemas)
            state.LogHint(
                "Platform and system schemas excluded. Comparison restricted to user schemas in source.");
        else
            state.LogHint(
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
                state);
        }

        // ─── 1. Compare tables (presence + row counts / checksums) ───
        await CompareTableDataAsync(
            state, sourceModel, destModel, sourceInfo, destInfo, mode, allItems, progress, waitWhilePaused, ct);

        // ─── 2. Compare schema objects via IModelComparer implementations ───
        state.Log("Comparing schema objects (schemas, indexes, views, functions, sequences, triggers, types, table DDL)...");
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

        LogObjectSummary(sorted, state);

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

    /// <summary>
    /// Restricts both models to the active table selection. The source filter is
    /// authoritative; the destination is restricted to the same table identities
    /// so unselected target tables are ignored rather than reported as
    /// differences. Views skipped because they depend on excluded tables are
    /// surfaced as Skipped items — consistent with Copy.
    /// </summary>
    private (DatabaseModel Source, DatabaseModel Destination) ApplyTableSelectionScope(
        DatabaseModel source,
        DatabaseModel destination,
        TableSelectionSpec spec,
        List<CompareResultItem> items,
        WorkflowState state)
    {
        var sourceResult = _filterApplier.Apply(source, spec);

        // Destination: exclude every table that is not part of the selected set.
        // Reuses the same filter engine so dependent-object handling stays identical.
        var selectedTables = sourceResult.FilteredModel.Tables
            .Select(t => new TableId(t.SchemaName, t.Name))
            .ToHashSet();
        var destExclusions = destination.Tables
            .Select(t => new TableId(t.SchemaName, t.Name))
            .Where(id => !selectedTables.Contains(id))
            .ToHashSet();
        var destResult = _filterApplier.Apply(destination, new TableSelectionSpec(true, destExclusions));

        state.Log(
            $"Table selection active: {sourceResult.Report.RemovedTables.Count} tables excluded, {sourceResult.FilteredModel.Tables.Count} tables in scope.");

        if (sourceResult.Report.StaleExclusions.Count > 0)
            state.LogWarning(
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
    // Model comparers delegation (OCP: new comparers are added without editing this class)
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
    // Table data comparison (row counts / checksums via ITableComparerProvider)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task CompareTableDataAsync(
        WorkflowState state,
        DatabaseModel sourceModel,
        DatabaseModel destModel,
        ConnectionInfo sourceInfo,
        ConnectionInfo destInfo,
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
        LogSchemaBreakdown(sourceTables, ECompareSide.Source.ToDisplayText(), state);
        LogSchemaBreakdown(destTables, ECompareSide.Destination.ToDisplayText(), state);

        var sourceTableDict = sourceTables.ToDictionary(t => $"{t.Schema}.{t.Name}", t => t);
        var destTableDict = destTables.ToDictionary(t => $"{t.Schema}.{t.Name}", t => t);

        var onlyInSource = sourceTables
            .Where(t => !destTableDict.ContainsKey($"{t.Schema}.{t.Name}")).ToList();
        var onlyInDest = destTables.Where(t => !sourceTableDict.ContainsKey($"{t.Schema}.{t.Name}"))
            .ToList();
        var commonTables = sourceTables
            .Where(t => destTableDict.ContainsKey($"{t.Schema}.{t.Name}")).ToList();

        state.Log(
            $"Tables: {commonTables.Count} in both, {onlyInSource.Count} missing in dest, {onlyInDest.Count} missing in source");

        if (commonTables.Count == 0 && sourceTables.Count > 0 && destTables.Count > 0)
        {
            state.LogWarning("The two databases share NO common tables.");
            state.LogWarning($"   Source has e.g.: {SampleNames(sourceTables)}");
            state.LogWarning($"   Destination has e.g.: {SampleNames(destTables)}");
            state.LogWarning(
                "   This usually means the source and destination connections are swapped, or one points at the wrong database. Please double-check both connections.");
        }
        else if (commonTables.Count > 0
                 && onlyInSource.Count + onlyInDest.Count > commonTables.Count)
        {
            state.LogWarning(
                $"More tables differ ({onlyInSource.Count + onlyInDest.Count}) than match ({commonTables.Count}). If this is unexpected, verify both connections point at the intended databases.");
        }

        // Track schemas where access is denied (and on which side) so we skip remaining tables silently
        var deniedSchemas = new Dictionary<string, ECompareSide>(StringComparer.OrdinalIgnoreCase);

        // Total items to process for progress calculation
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
                    sourceCount = await _tableComparerProvider.CountRowsAsync(
                                      sourceInfo,
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
                    state.LogWarning(
                        $"Permission denied for schema {schema} — skipping remaining tables in this schema.");
                }
                catch (Exception ex)
                {
                    sourceCountError = ex.Message;
                    _logger.LogWarning(
                        ex,
                        "Failed to count rows for {Table} in source",
                        qualifiedName);
                    state.LogWarning($"Could not count rows for {qualifiedName}: {ex.Message}");
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
                    destCount = await _tableComparerProvider.CountRowsAsync(
                                    destInfo,
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
                    state.LogWarning(
                        $"Permission denied for schema {schema} — skipping remaining tables in this schema.");
                }
                catch (Exception ex)
                {
                    destCountError = ex.Message;
                    _logger.LogWarning(
                        ex,
                        "Failed to count rows for {Table} in destination",
                        qualifiedName);
                    state.LogWarning($"Could not count rows for {qualifiedName}: {ex.Message}");
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

            // Skip tables in schemas already known to be inaccessible
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

            state.Log($"Comparing {qualifiedName} ({processedItems + 1}/{totalItems})...");

            try
            {
                var result = await _tableComparerProvider.CompareTableAsync(
                                 sourceInfo,
                                 destInfo,
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
                state.LogWarning(
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
                state.LogError($"Could not compare {qualifiedName}: {ex.Message}");
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

        // Log a summary of skipped schemas
        if (skippedBySchema.Count > 0)
        {
            var summary = string.Join(
                ", ",
                skippedBySchema.Select(kv => $"{kv.Key} ({kv.Value} tables)"));
            state.LogWarning($"Skipped due to insufficient permissions: {summary}");
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

    private static void LogObjectSummary(List<CompareResultItem> sorted, WorkflowState state)
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
                state.LogError(summary);
            else
                state.Log(summary);

            if (missingDest > 0)
            {
                foreach (var item in group.Where(i => i.Status == ECompareStatus.MissingDest))
                    state.LogError($"  {item.TableName}: missing in destination");
            }

            if (missingSrc > 0)
            {
                foreach (var item in group.Where(i => i.Status == ECompareStatus.MissingSource))
                    state.LogError($"  {item.TableName}: missing in source");
            }

            if (different > 0)
            {
                foreach (var item in group.Where(i => i.Status == ECompareStatus.Different))
                    state.LogError($"  {item.TableName}: {item.Details}");
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
        WorkflowState state)
    {
        if (tables.Count == 0)
            return;

        var breakdown = tables
            .GroupBy(t => t.Schema)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key}: {g.Count()}");

        state.Log($"{side} schemas: {string.Join(", ", breakdown)}");
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
