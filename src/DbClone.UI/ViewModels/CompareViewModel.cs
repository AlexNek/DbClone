using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.Enums;
using DbClone.UI.Models;
using DbClone.UI.Services;
using DbClone.UI.Settings;

using Microsoft.Extensions.Logging;

using Wpf.Ui.Controls;

namespace DbClone.UI.ViewModels;

/// <summary>
/// Owns all database comparison state and logic.
/// Exposed as MainViewModel.Compare; ComparisonResultsView binds to it.
/// Also satisfies the ProgressView binding contract when in Compare mode.
/// </summary>
public sealed partial class CompareViewModel : ObservableObject, IWorkflowViewModel
{
    /// <summary>Sentinel option: no schema filter. Never null — WPF ComboBox cannot select a null item.</summary>
    private static readonly CompareFilterOption<string?> AllSchemasOption = new("All Schemas", null);

    /// <summary>Sentinel option: no object-type filter.</summary>
    private static readonly CompareFilterOption<EDatabaseObjectType?> AllTypesOption =
        new("All Types", null);

    private readonly List<CompareResultItem> _allCompareResultItems = [];

    private readonly IDatabaseComparerService _comparerService;

    private readonly ManualResetEventSlim _comparePauseGate = new(true);

    private readonly OperationContext _ctx;

    private readonly IDatabaseService _dbService;

    private readonly IDialogService _dialogService;

    private readonly ILogger<CompareViewModel> _logger;

    private readonly ReportExportService _reportExportService;

    private readonly SettingsPersistenceManager _settingsPersister;

    private readonly ViewModelStateManager _stateManager;

    private CancellationTokenSource? _compareCts;

    [ObservableProperty]
    private string _compareDuration = "";

    [ObservableProperty]
    private int _compareProgressPercent;

    [ObservableProperty]
    private InfoBarSeverity _compareStatusSeverity;

    [ObservableProperty]
    private string _compareVerifyMode = "";

    /// <summary>Immutable summary line for the results banner — owned by compare, never overwritten by other operations (e.g. Validate).</summary>
    [ObservableProperty]
    private string _compareSummary = "";

    // ── ProgressView binding contract properties ────────────────────────────────

    [ObservableProperty]
    private string _currentPhase = "Ready";

    [ObservableProperty]
    private string _currentTable = "\u2014";

    [ObservableProperty]
    private string _currentlyComparing = "";

    [ObservableProperty]
    private bool _hasCompareResults;

    [ObservableProperty]
    private bool _isCompareRunning;

    [ObservableProperty]
    private bool _isComparePaused;

    [ObservableProperty]
    private bool _isComparing;

    /// <summary>Controls table-detail panel visibility in ProgressView. True during table comparison phase.</summary>
    [ObservableProperty]
    private bool _isCopyingData;

    /// <summary>Human-readable breakdown of missing-in-dest objects by type (e.g. "55 tables, 88 indexes").</summary>
    [ObservableProperty]
    private string _missingDestBreakdown = "";

    /// <summary>Human-readable breakdown of missing-in-source objects by type.</summary>
    [ObservableProperty]
    private string _missingSourceBreakdown = "";

    [ObservableProperty]
    private string _overallCompareStatus = "";

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string _reportGeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    [ObservableProperty]
    private long _rowsProcessed;

    [ObservableProperty]
    private List<CompareFilterOption<string?>> _schemaFilters = [AllSchemasOption];

    [ObservableProperty]
    private ECompareStatus? _selectedCompareFilter;

    [ObservableProperty]
    private CompareResultItem? _selectedCompareResultItem;

    [ObservableProperty]
    private CompareFilterOption<EDatabaseObjectType?>? _selectedObjectTypeFilter = AllTypesOption;

    [ObservableProperty]
    private CompareFilterOption<string?>? _selectedSchemaFilter = AllSchemasOption;

    [ObservableProperty]
    private double _tableProgressPercent;

    [ObservableProperty]
    private int _totalCompared;

    [ObservableProperty]
    private int _totalDifferent;

    [ObservableProperty]
    private int _totalErrors;

    [ObservableProperty]
    private int _totalIdentical;

    [ObservableProperty]
    private int _totalMissingDest;

    [ObservableProperty]
    private int _totalMissingSource;

    /// <summary>Objects that match but carry a non-structural note (e.g. schema owner differs).</summary>
    [ObservableProperty]
    private int _totalNotices;

    [ObservableProperty]
    private long _totalRows;

    [ObservableProperty]
    private int _totalSkipped;

    public bool CanExport => HasCompareResults;

    public bool CanViewReport => HasCompareResults;

    public ObservableCollection<CompareResultItem> CompareResultItems { get; } = [];

    /// <summary>Always hidden for compare — no meaningful transfer speed.</summary>
    public bool IsDataTransferStatsVisible => false;

    /// <summary>Whether the comparison is currently running (IWorkflowViewModel).</summary>
    public bool IsRunning => IsCompareRunning;

    /// <summary>Per-workflow UI state (logs, banner, status, objects panel).</summary>
    public WorkflowState State { get; }

    ICommand IWorkflowViewModel.PauseCommand => PauseCompareCommand;

    ICommand IWorkflowViewModel.StopCommand => StopCompareCommand;

    /// <summary>Static "—" — compare has no transfer speed metric.</summary>
    public string TransferSpeed => "\u2014";

    /// <summary>Static "—" — compare has no meaningful ETA.</summary>
    public string EstimatedTimeRemaining => "\u2014";

    public bool HasComparisonResult => HasCompareResults;

    // Instance property (not static): WPF {Binding} silently ignores static properties.
    public IReadOnlyList<CompareFilterOption<EDatabaseObjectType?>> ObjectTypeFilters { get; } =
        [
            AllTypesOption,
            new("Schemas", EDatabaseObjectType.Schema),
            new("Tables", EDatabaseObjectType.Table),
            new("Indexes", EDatabaseObjectType.Index),
            new("Views", EDatabaseObjectType.View),
            new("Materialized Views", EDatabaseObjectType.MaterializedView),
            new("Functions", EDatabaseObjectType.Function),
            new("Sequences", EDatabaseObjectType.Sequence),
            new("Triggers", EDatabaseObjectType.Trigger),
            new("Enums", EDatabaseObjectType.Enum),
            new("Domains", EDatabaseObjectType.Domain),
            new("Composite Types", EDatabaseObjectType.CompositeType)
        ];

    public UserSettings Settings { get; }

    public CompareViewModel(
        ILogger<CompareViewModel> logger,
        IDatabaseComparerService comparerService,
        IDatabaseService dbService,
        IDialogService dialogService,
        ReportExportService reportExportService,
        SettingsPersistenceManager settingsPersister,
        ViewModelStateManager stateManager,
        UserSettings settings,
        OperationContext ctx)
    {
        _logger = logger;
        _comparerService = comparerService;
        _dbService = dbService;
        _dialogService = dialogService;
        _reportExportService = reportExportService;
        _settingsPersister = settingsPersister;
        _stateManager = stateManager;
        Settings = settings;
        _ctx = ctx;
        State = new WorkflowState();
    }

    private void ApplyCompareFilter()
    {
        IEnumerable<CompareResultItem> filtered = _allCompareResultItems;

        if (SelectedSchemaFilter?.Value is { } schemaName)
        {
            filtered = filtered.Where(i => string.Equals(
                i.SchemaName,
                schemaName,
                StringComparison.OrdinalIgnoreCase));
        }

        if (SelectedObjectTypeFilter?.Value is { } objectType)
        {
            filtered = filtered.Where(i => i.ObjectType == objectType);
        }

        filtered = SelectedCompareFilter switch
            {
                ECompareStatus.Different =>
                    filtered.Where(i => i.Status == ECompareStatus.Different),
                ECompareStatus.MissingSource => filtered.Where(i =>
                    i.Status is ECompareStatus.MissingSource or ECompareStatus.MissingDest),
                ECompareStatus.Skipped => filtered.Where(i => i.Status == ECompareStatus.Skipped),
                ECompareStatus.Error => filtered.Where(i => i.Status == ECompareStatus.Error),
                ECompareStatus.Notice =>
                    filtered.Where(i => i.Status == ECompareStatus.Notice),
                ECompareStatus.Identical => filtered.Where(i =>
                    i.Status is ECompareStatus.Identical or ECompareStatus.Notice),
                _ => filtered,
            };

        CompareResultItems.Clear();
        foreach (var item in filtered)
            CompareResultItems.Add(item);
    }

    private ComparisonReportData BuildReportData()
    {
        return new ComparisonReportData(
                [.. CompareResultItems],
            _ctx.Source.Summary,
            _ctx.Destination.Summary,
            TotalIdentical,
            TotalNotices,
            TotalDifferent,
            TotalMissingSource,
            TotalMissingDest,
            TotalSkipped,
            TotalErrors,
            CompareDuration);
    }

    /// <summary>
    /// Builds a human-readable breakdown like "55 tables, 88 indexes" for a given status.
    /// Returns empty string when there are no items with that status.
    /// </summary>
    private static string BuildTypeBreakdown(List<CompareResultItem> items, ECompareStatus status)
    {
        var groups = items
            .Where(i => i.Status == status)
            .GroupBy(i => i.ObjectType)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {Pluralize(g.Key, g.Count())}");

        return string.Join(", ", groups);
    }

    private bool CanStartCompare() => !_ctx.IsBusy;

    [RelayCommand]
    private void ClearCompareResults()
    {
        CompareResultItems.Clear();
        _allCompareResultItems.Clear();
        HasCompareResults = false;
        OverallCompareStatus = "";
        TotalCompared = 0;
        TotalIdentical = 0;
        TotalNotices = 0;
        TotalDifferent = 0;
        TotalMissingSource = 0;
        TotalMissingDest = 0;
        TotalSkipped = 0;
        TotalErrors = 0;
        MissingDestBreakdown = "";
        MissingSourceBreakdown = "";
        CompareDuration = "";
    }

    [RelayCommand]
    private void PauseCompare()
    {
        if (!_ctx.IsBusy || !IsCompareRunning) return;

        if (_comparePauseGate.IsSet)
        {
            _comparePauseGate.Reset();
            IsComparePaused = true;
            State.Log("Comparison paused");
        }
        else
        {
            _comparePauseGate.Set();
            IsComparePaused = false;
            State.Log("Comparison resumed");
        }
    }

    [RelayCommand]
    private void StopCompare()
    {
        if (_compareCts is { IsCancellationRequested: false })
        {
            State.Log("Stop requested — cancelling comparison...");
            _compareCts.Cancel();
        }
    }

    private async Task WaitWhilePaused(CancellationToken ct)
    {
        if (_comparePauseGate.IsSet) return;
        await Task.Run(() => _comparePauseGate.Wait(ct), ct);
    }

    private void OnCompareProgress(CompareProgressInfo info)
    {
        ProgressPercent = info.PercentComplete;
        CompareProgressPercent = info.PercentComplete;
        CurrentPhase = info.CurrentPhase;
        CurrentTable = string.IsNullOrEmpty(info.CurrentTable) ? "\u2014" : info.CurrentTable;
        CurrentlyComparing = info.CurrentTable;

        // Show table-detail section during table comparison phase
        var isTablePhase = info.CurrentPhase == "Comparing tables" && info.TotalTables > 0;
        IsCopyingData = isTablePhase;

        if (isTablePhase)
        {
            RowsProcessed = info.TablesProcessed;
            TotalRows = info.TotalTables;
            TableProgressPercent = info.TotalTables > 0
                ? info.TablesProcessed * 100.0 / info.TotalTables
                : 0;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartCompare))]
    private async Task CompareDatabasesAsync(CancellationToken cancellationToken)
    {
        _settingsPersister.SaveNow();

        _compareCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _compareCts.Token;

        _logger.LogInformation("Compare operation started");
        _ctx.IsBusy = true;
        IsComparing = true;
        IsCompareRunning = true;
        State.BeginNewRun();
        HasCompareResults = false;
        CompareResultItems.Clear();
        _allCompareResultItems.Clear();
        CompareProgressPercent = 0;
        ProgressPercent = 0;
        OverallCompareStatus = "";
        CurrentlyComparing = "";
        CurrentPhase = "Starting...";
        CurrentTable = "\u2014";
        IsCopyingData = false;
        TableProgressPercent = 0;
        RowsProcessed = 0;
        TotalRows = 0;

        State.Log($"Starting comparison: {_ctx.Source.Summary} → {_ctx.Destination.Summary}");

        _stateManager.BeginOperation();

        try
        {
            // ─── Validate that both connections specify a database ───
            if (string.IsNullOrWhiteSpace(_ctx.Source.DatabaseName))
            {
                ShowValidationError(
                    "Source connection has no database name. Comparison requires a specific database.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_ctx.Destination.DatabaseName))
            {
                ShowValidationError(
                    "Destination connection has no database name. Comparison requires a specific database — backup-only connections cannot be compared.");
                return;
            }

            State.StatusMessage = "Checking source connection...";
            var sourceVersion =
                await _dbService.TestConnectionAsync(_ctx.Source, ct);
            if (sourceVersion == null)
            {
                State.LogError("Failed to connect to source");
                return;
            }

            State.Log($"Connected to source ({_ctx.Source.Summary})");

            State.StatusMessage = "Checking destination connection...";
            var destVersion = await _dbService.TestConnectionAsync(
                                  _ctx.Destination,
                                  ct);
            if (destVersion == null)
            {
                State.LogError($"Failed to connect to destination ({_ctx.Destination.Summary})");
                return;
            }

            State.Log($"Connected to destination ({_ctx.Destination.Summary})");

            State.StatusMessage = "Reading schema...";
            var metadata =
                await _dbService.ReadDatabaseMetadataAsync(_ctx.Source, ct);
            State.ObjectsPanel.SetCount(EDatabaseObjectType.Table, metadata.Tables);
            State.ObjectsPanel.SetCount(EDatabaseObjectType.View, metadata.Views + metadata.MaterializedViews);
            State.ObjectsPanel.SetCount(EDatabaseObjectType.Sequence, metadata.Sequences);
            State.ObjectsPanel.SetCount(EDatabaseObjectType.Function, metadata.Functions);
            State.ObjectsPanel.SetCount(EDatabaseObjectType.Trigger, metadata.Triggers);
            State.ObjectsPanel.SetCount(EDatabaseObjectType.Enum, metadata.Enums);
            State.ObjectsPanel.SetCount(EDatabaseObjectType.Domain, metadata.Domains);
            State.ObjectsPanel.SetCount(EDatabaseObjectType.CompositeType, metadata.CompositeTypes);
            State.ObjectsPanel.SetCount(EDatabaseObjectType.Index, metadata.Indexes);
            State.ObjectsPanel.SetCount(EDatabaseObjectType.Constraint, metadata.Constraints);
            State.Log($"Schema objects: {metadata.Summary}");

            State.StatusMessage = "Comparing databases...";
            var compareProgress = new Progress<CompareProgressInfo>(OnCompareProgress);
            var result = await _comparerService.CompareDatabasesAsync(
                             _ctx.Source,
                             _ctx.Destination,
                             State,
                             Settings.SelectedVerifyMode,
                             !Settings.ComparePlatformSchemas,
                             compareProgress,
                             WaitWhilePaused,
                             ct);

            _allCompareResultItems.Clear();
            _allCompareResultItems.AddRange(result.Items);

            var schemas = result.Items
                .Select(i => i.SchemaName)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(s => new CompareFilterOption<string?>(s, s));
            SchemaFilters = [AllSchemasOption, .. schemas];
            SelectedSchemaFilter = AllSchemasOption;
            SelectedObjectTypeFilter = AllTypesOption;

            ApplyCompareFilter();

            HasCompareResults = true;
            TotalCompared = result.Items.Count;
            TotalIdentical = result.TotalIdentical;
            TotalNotices = result.TotalNotices;
            TotalDifferent = result.TotalDifferent;
            TotalMissingSource = result.TotalMissingSource;
            TotalMissingDest = result.TotalMissingDest;
            TotalErrors = result.TotalErrors;
            TotalSkipped = result.TotalSkipped;
            MissingDestBreakdown = BuildTypeBreakdown(result.Items, ECompareStatus.MissingDest);
            MissingSourceBreakdown = BuildTypeBreakdown(result.Items, ECompareStatus.MissingSource);
            FinalizeObjectsPanel(result.Items);

            var hasDifferences = TotalDifferent > 0 || TotalMissingSource > 0
                                                    || TotalMissingDest > 0 || TotalErrors > 0;
            OverallCompareStatus = hasDifferences
                                       ? "Comparison complete — Databases differ"
                                       : TotalSkipped > 0
                                           ? "Comparison complete — no differences in comparable objects"
                                           : "Comparison complete — Databases are identical";
            CompareStatusSeverity = hasDifferences
                                        ? InfoBarSeverity.Error
                                        : TotalSkipped > 0
                                            ? InfoBarSeverity.Warning
                                            : InfoBarSeverity.Success;
            CompareDuration = State.ElapsedTime;
            CompareVerifyMode = Settings.SelectedVerifyMode.ToString();

            State.StatusMessage = "Comparison complete";
            CurrentPhase = "Complete";
            ProgressPercent = 100;
            CompareProgressPercent = 100;
            IsCopyingData = false;
            CompareSummary =
                $"Comparison: {TotalIdentical} identical, {TotalDifferent} different, {TotalMissingSource} missing src, {TotalMissingDest} missing dst, {TotalSkipped} skipped, {TotalErrors} errors";
            State.StatusBarSummary = CompareSummary;

            // Log error-level summary so the log is consistent with the red UI status
            if (hasDifferences)
            {
                var parts = new List<string>();
                if (TotalDifferent > 0) parts.Add($"{TotalDifferent} different");
                if (TotalMissingDest > 0) parts.Add($"{TotalMissingDest} missing in destination");
                if (TotalMissingSource > 0) parts.Add($"{TotalMissingSource} missing in source");
                if (TotalErrors > 0) parts.Add($"{TotalErrors} errors");
                State.LogError($"Comparison finished with problems: {string.Join(", ", parts)}");
            }
            else
            {
                State.Log($"Comparison finished — {OverallCompareStatus}");
            }

            // Report schemas where permission was denied — actionable guidance for the user
            LogDeniedSchemas(result.Items);
        }
        catch (OperationCanceledException)
        {
            State.StatusMessage = "Cancelled";
            CurrentPhase = "Cancelled";
            State.Log("Comparison cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compare operation failed");
            State.StatusMessage = $"Failed: {ex.Message}";
            CurrentPhase = "Error";
            State.LastError = ex.Message;
            State.LogError(ex.Message);
        }
        finally
        {
            _stateManager.EndOperation(EOperationState.Completed);
            _ctx.IsBusy = false;
            IsComparing = false;
            IsCompareRunning = false;
            IsComparePaused = false;
            _comparePauseGate.Set();
            IsCopyingData = false;
            CurrentlyComparing = "";
            _compareCts?.Dispose();
            _compareCts = null;
        }
    }

    [RelayCommand]
    private void CopyAllCompareResults()
    {
        if (CompareResultItems.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Type\tObject\tStatus\tSource Rows\tDest Rows\tDetails");
        foreach (var item in CompareResultItems)
        {
            sb.AppendLine(
                $"{item.ObjectTypeDisplay}\t{item.TableName}\t{item.StatusText}\t{item.SourceCountDisplay}\t{item.DestCountDisplay}\t{item.Details}");
        }

        Clipboard.SetText(sb.ToString());
    }

    [RelayCommand]
    private void CopyCompareRow()
    {
        if (SelectedCompareResultItem is { } item)
        {
            Clipboard.SetText(
                $"{item.ObjectTypeDisplay}\t{item.TableName}\t{item.StatusText}\t{item.SourceCountDisplay}\t{item.DestCountDisplay}\t{item.Details}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void CopyToClipboard()
    {
        var text = _reportExportService.Export(".txt", BuildReportData());
        Clipboard.SetText(text);
        _logger.LogInformation("Copied comparison results to clipboard");
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportToHtml()
    {
        var path = _dialogService.SaveFile("HTML File|*.html", "comparison-report.html");
        if (string.IsNullOrEmpty(path)) return;

        var html = _reportExportService.Export(Path.GetExtension(path), BuildReportData());
        File.WriteAllText(path, html);
        _logger.LogInformation("Exported HTML report to {Path}", path);
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportToJson()
    {
        var path = _dialogService.SaveFile("JSON File|*.json", "comparison-report.json");
        if (string.IsNullOrEmpty(path)) return;

        var json = _reportExportService.Export(Path.GetExtension(path), BuildReportData());
        File.WriteAllText(path, json);
        _logger.LogInformation("Exported JSON report to {Path}", path);
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportToMarkdown()
    {
        var path = _dialogService.SaveFile("Markdown File|*.md", "comparison-report.md");
        if (string.IsNullOrEmpty(path)) return;

        var md = _reportExportService.Export(Path.GetExtension(path), BuildReportData());
        File.WriteAllText(path, md);
        _logger.LogInformation("Exported Markdown report to {Path}", path);
    }

    [RelayCommand]
    private void FilterCompareAll() => SelectedCompareFilter = null;

    [RelayCommand]
    private void FilterCompareDifferent() => SelectedCompareFilter = ECompareStatus.Different;

    [RelayCommand]
    private void FilterCompareErrors() => SelectedCompareFilter = ECompareStatus.Error;

    [RelayCommand]
    private void FilterCompareIdentical() => SelectedCompareFilter = ECompareStatus.Identical;

    [RelayCommand]
    private void FilterCompareMissing() => SelectedCompareFilter = ECompareStatus.MissingSource;

    [RelayCommand]
    private void FilterCompareSkipped() => SelectedCompareFilter = ECompareStatus.Skipped;

    partial void OnHasCompareResultsChanged(bool value)
    {
        OnPropertyChanged(nameof(HasComparisonResult));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanViewReport));
        ViewReportCommand.NotifyCanExecuteChanged();
        ExportToHtmlCommand.NotifyCanExecuteChanged();
        ExportToMarkdownCommand.NotifyCanExecuteChanged();
        ExportToJsonCommand.NotifyCanExecuteChanged();
        CopyToClipboardCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedCompareFilterChanged(ECompareStatus? value) => ApplyCompareFilter();

    partial void OnSelectedObjectTypeFilterChanged(
        CompareFilterOption<EDatabaseObjectType?>? value) =>
        ApplyCompareFilter();

    partial void OnSelectedSchemaFilterChanged(CompareFilterOption<string?>? value) =>
        ApplyCompareFilter();

    private static string Pluralize(EDatabaseObjectType type, int count)
    {
        var singular = type switch
            {
                EDatabaseObjectType.Schema => "schema",
                EDatabaseObjectType.Table => "table",
                EDatabaseObjectType.Index => "index",
                EDatabaseObjectType.View => "view",
                EDatabaseObjectType.MaterializedView => "materialized view",
                EDatabaseObjectType.Function => "function",
                EDatabaseObjectType.Sequence => "sequence",
                EDatabaseObjectType.Trigger => "trigger",
                EDatabaseObjectType.Enum => "enum",
                EDatabaseObjectType.Domain => "domain",
                EDatabaseObjectType.CompositeType => "composite type",
                _ => type.ToString().ToLowerInvariant()
            };
        return count == 1 ? singular : singular + (singular.EndsWith("x") ? "es" : "s");
    }

    /// <summary>
    /// Marks each objects-panel entry as Done or Failed based on comparison results,
    /// so the strip reflects the finished state (mirrors CopyProgressUIListener for copy).
    /// </summary>
    private void FinalizeObjectsPanel(List<CompareResultItem> items)
    {
        var panelTypes = new[]
        {
            EDatabaseObjectType.Table,
            EDatabaseObjectType.View,
            EDatabaseObjectType.Sequence,
            EDatabaseObjectType.Function,
            EDatabaseObjectType.Trigger,
            EDatabaseObjectType.Enum,
            EDatabaseObjectType.Domain,
            EDatabaseObjectType.CompositeType,
            EDatabaseObjectType.Index,
            EDatabaseObjectType.Constraint
        };

        foreach (var type in panelTypes)
        {
            // Views and materialized views are compared together under the View panel entry.
            var hasProblems = items.Any(i =>
                (i.ObjectType == type
                 || (type == EDatabaseObjectType.View
                     && i.ObjectType == EDatabaseObjectType.MaterializedView))
                && i.Status is ECompareStatus.Different or ECompareStatus.MissingDest
                    or ECompareStatus.MissingSource or ECompareStatus.Error);

            if (hasProblems)
                State.ObjectsPanel.SetFailed(type);
            else
                State.ObjectsPanel.SetDone(type);
        }
    }

    /// <summary>Shows a validation error as a prominent red InfoBar banner + log entry.</summary>
    private void ShowValidationError(string message)
    {
        State.LogError(message);
        State.LastError = message;
        State.StatusMessage = "Comparison blocked";
        State.StatusBarSummary = message;
    }

    [RelayCommand(CanExecute = nameof(CanViewReport))]
    private void ViewReport()
    {
        ReportGeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var window = new Views.ReportWindow(this);
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();
    }

    /// <summary>
    /// Logs user-facing messages for schemas where permission was denied during comparison.
    /// Derives denied-schema info from skipped items — no extra state needed from the service.
    /// </summary>
    private void LogDeniedSchemas(List<CompareResultItem> items)
    {
        var deniedItems = items
            .Where(i => i.SkipReason == ESkipReason.PermissionDenied && i.SkipSide.HasValue)
            .GroupBy(i => i.SkipSide!.Value)
            .OrderBy(g => g.Key);

        foreach (var group in deniedItems)
        {
            var side = group.Key.ToDisplayText();
            var schemas = string.Join(
                ", ",
                group.Select(i => i.SchemaName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            State.LogError(
                $"The {side} connection cannot read schema(s): {schemas}.");
            State.LogError(
                "   Table data in these schemas could not be compared.");
            State.LogError(
                "   Grant SELECT on all tables, or use a connection with read access to all schemas.");
        }
    }
}
