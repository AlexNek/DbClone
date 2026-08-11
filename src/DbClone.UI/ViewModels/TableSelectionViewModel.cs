using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.Application.TableFilter;
using DbClone.UI.Models;
using DbClone.UI.Services;

using Serilog;

namespace DbClone.UI.ViewModels;

/// <summary>
/// View model of the table selection dialog: master-detail layout with
/// schema tri-state checkboxes, searchable/sortable table grid, preset management,
/// relationship explorer and the non-blocking validation summary on Apply.
/// </summary>
public sealed partial class TableSelectionViewModel : ObservableObject
{
    private readonly IDatabaseService _dbService;

    private readonly IDialogService _dialogService;

    private readonly ITableFilterApplier _filterApplier;

    private readonly ITableSelectionPresetNameValidator _presetNameValidator;

    private readonly ITableSelectionPresetStore _presetStore;

    private readonly ITableSelectionService _selectionService;

    private readonly ConnectionViewModel _sourceConnection;

    private readonly List<TableSelectionItem> _allItems = [];

    private readonly Dictionary<TableId, TableSelectionItem> _itemsById = [];

    private HashSet<TableId> _loadedExclusions = [];

    private string? _loadedPresetId;

    private DatabaseModel? _model;

    private bool _suspendItemRecount;

    private bool _suspendCascadeDeselect;

    private bool _suspendSchemaSync;

    private string _sortColumn = "Schema";

    private bool _sortDescending;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string? _loadError;

    [ObservableProperty]
    private string _relationshipDetails = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedCountText = string.Empty;

    [ObservableProperty]
    private TableSelectionItem? _selectedTable;

    [ObservableProperty]
    private SchemaNode? _selectedSchemaNode;

    [ObservableProperty]
    private PresetOption? _selectedPreset;

    [ObservableProperty]
    private bool _showValidationSummary;

    [ObservableProperty]
    private string _tablesHeader = "TABLES — All Schemas";

    /// <summary>Initializes a new instance. Call <see cref="LoadAsync"/> before showing.</summary>
    public TableSelectionViewModel(
        ITableSelectionService selectionService,
        ITableSelectionPresetStore presetStore,
        IDialogService dialogService,
        IDatabaseService dbService,
        ITableFilterApplier filterApplier,
        ITableSelectionPresetNameValidator presetNameValidator,
        ConnectionViewModel sourceConnection)
    {
        _selectionService = selectionService;
        _presetStore = presetStore;
        _dialogService = dialogService;
        _dbService = dbService;
        _filterApplier = filterApplier;
        _presetNameValidator = presetNameValidator;
        _sourceConnection = sourceConnection;
    }

    /// <summary>True when the current checkbox state differs from the loaded preset.</summary>
    public bool IsDialogDirty => !GetCurrentExclusions().SetEquals(_loadedExclusions);

    /// <summary>True while "All Tables" is the loaded preset.</summary>
    public bool IsAllTablesLoaded => _loadedPresetId is null;

    /// <summary>Show the Schema column — only in the "All Schemas" view.</summary>
    public bool ShowSchemaColumn => SelectedSchemaNode?.Name is null;

    /// <summary>True when a named preset is loaded (Save/Rename/Delete enabled).</summary>
    public bool IsNamedPresetLoaded => _loadedPresetId is not null;

    public ObservableCollection<PresetOption> PresetOptions { get; } = [];

    public ObservableCollection<SchemaNode> SchemaNodes { get; } = [];

    public ObservableCollection<TableSelectionItem> VisibleTables { get; } = [];

    /// <summary>Issue groups for the validation summary panel.</summary>
    public IReadOnlyList<ValidationSection> ValidationSections { get; private set; } = [];

    /// <summary>
    /// Reads the source model over a live connection and initializes the grid.
    /// On failure the dialog shows a prominent error state.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        LoadError = null;

        try
        {
            _model = await _dbService.ReadSourceModelAsync(_sourceConnection, ct);

            BuildItems();
            BuildSchemaNodes();
            ApplyActiveSelection();
            BuildPresetOptions();
            UpdateCounts();
            RefreshVisibleTables();

            _ = LoadSizesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "[TableSelectionDialog] Failed to read source model");
            LoadError = $"Cannot read tables from the source database: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Apply pipeline ─────────────────────────────────────────────────────────

    /// <summary>FR11 — an operation cannot start with zero selected tables.</summary>
    public bool IsSelectionEmpty => _model is not null && _model.Tables.Count > 0 && SelectedCount == 0;

    public int SelectedCount => _allItems.Count(i => i.IsSelected);

    /// <summary>
    /// Runs the dependency check on the current selection and populates
    /// <see cref="ValidationSections"/>. Returns true when issues exist.
    /// </summary>
    public bool HasValidationIssues()
    {
        if (_model is null) return false;

        var exclusions = GetCurrentExclusions();
        if (exclusions.Count == 0)
        {
            ValidationSections = [];
            return false;
        }

        var report = _filterApplier.Apply(_model, new TableSelectionSpec(true, exclusions)).Report;

        var sections = new List<ValidationSection>(3);

        if (report.DroppedForeignKeys.Count > 0)
        {
            sections.Add(new ValidationSection(
                $"Foreign keys ({report.DroppedForeignKeys.Count})",
                [.. report.DroppedForeignKeys.Select(fk =>
                    $"{Display(fk.OwningTable)}.{fk.ConstraintName} → {Display(fk.ReferencedTable)} (excluded)")]));
        }

        if (report.SkippedViews.Count > 0)
        {
            sections.Add(new ValidationSection(
                $"Views ({report.SkippedViews.Count})",
                [.. report.SkippedViews.Select(v => $"{Display(v)} (depends on an excluded table)")]));
        }

        if (report.OrphanedPartitions.Count > 0)
        {
            sections.Add(new ValidationSection(
                $"Partitions ({report.OrphanedPartitions.Count})",
                [.. report.OrphanedPartitions.Select(p => $"{Display(p)} (parent is excluded)")]));
        }

        ValidationSections = sections;
        return sections.Count > 0;
    }

    /// <summary>Commits the dialog selection to the shared selection service.</summary>
    public async Task CommitAsync()
    {
        await _selectionService.ApplyDialogSelectionAsync(_loadedPresetId, GetCurrentExclusions());
    }

    public HashSet<TableId> GetCurrentExclusions() =>
        [.. _allItems.Where(i => !i.IsSelected).Select(i => i.Id)];

    // ── Commands ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SelectAll() => SetAllSelected(true);

    [RelayCommand]
    private void SelectNone() => SetAllSelected(false);

    [RelayCommand]
    private void SortBy(string column)
    {
        if (string.Equals(_sortColumn, column, StringComparison.Ordinal))
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = column == "Size"; // large-first is the useful direction
        }

        RefreshVisibleTables();
    }

    [RelayCommand]
    private void HideValidationSummary() => ShowValidationSummary = false;

    // ── Preset management ──────────────────────────────────────────────────────

    /// <summary>Overwrites the loaded preset with the current checkbox state.</summary>
    public async Task<string?> SavePresetAsync()
    {
        if (IsSelectionEmpty)
            return "Cannot save a preset with zero selected tables.";

        var database = _selectionService.CurrentDatabase;
        if (database is null || _loadedPresetId is null) return null;

        var existing = await _presetStore.GetPresetAsync(database, _loadedPresetId);
        if (existing is null) return null;

        var updated = existing with
        {
            ExcludedTables = GetCurrentExclusions(),
            ModifiedAt = DateTime.Now
        };

        await _presetStore.SavePresetAsync(database, updated);
        await _selectionService.ReloadPresetsAsync();

        _loadedExclusions = GetCurrentExclusions();
        BuildPresetOptions();
        return null;
    }

    /// <summary>
    /// Creates a new preset from the current checkbox state.
    /// Returns a validation error, or null on success.
    /// </summary>
    public async Task<string?> SaveAsPresetAsync(string name)
    {
        if (IsSelectionEmpty)
            return "Cannot save a preset with zero selected tables.";

        var database = _selectionService.CurrentDatabase;
        if (database is null) return "No source database selected.";

        var error = _presetNameValidator.Validate(
            name, _selectionService.Presets);
        if (error is not null) return error;

        var preset = TableSelectionPreset.Create(name.Trim(), GetCurrentExclusions());
        await _presetStore.SavePresetAsync(database, preset);
        await _selectionService.ReloadPresetsAsync();

        SetLoadedPreset(preset.Id, GetCurrentExclusions());
        BuildPresetOptions();
        return null;
    }

    /// <summary>Renames the loaded preset. Returns a validation error, or null on success.</summary>
    public async Task<string?> RenamePresetAsync(string newName)
    {
        var database = _selectionService.CurrentDatabase;
        if (database is null || _loadedPresetId is null) return "No preset selected.";

        var error = _presetNameValidator.Validate(
            newName, _selectionService.Presets, _loadedPresetId);
        if (error is not null) return error;

        await _presetStore.RenamePresetAsync(database, _loadedPresetId, newName);
        await _selectionService.ReloadPresetsAsync();

        BuildPresetOptions();
        return null;
    }

    /// <summary>Deletes the loaded preset after confirmation. Working checkboxes are kept.</summary>
    public async Task DeletePresetAsync()
    {
        var database = _selectionService.CurrentDatabase;
        if (database is null || _loadedPresetId is null) return;

        var preset = _selectionService.Presets.FirstOrDefault(p => p.Id == _loadedPresetId);
        var message = $"Delete preset '{preset?.Name}'? This cannot be undone.";

        if (IsDialogDirty)
        {
            message += "\n\nThe dialog also has unsaved modifications — they will be discarded.";
        }

        if (!await _dialogService.ConfirmAsync("Delete Preset", message)) return;

        await _presetStore.DeletePresetAsync(database, _loadedPresetId);
        await _selectionService.ReloadPresetsAsync();

        SetLoadedPreset(null, []);
        BuildPresetOptions();
    }

    /// <summary>Name of the loaded preset for pre-filled prompts.</summary>
    public string? LoadedPresetName =>
        _loadedPresetId is null
            ? null
            : _selectionService.Presets.FirstOrDefault(p => p.Id == _loadedPresetId)?.Name;

    // ── Change handlers ────────────────────────────────────────────────────────

    partial void OnSearchTextChanged(string value) => RefreshVisibleTables();

    partial void OnSelectedSchemaNodeChanged(SchemaNode? value)
    {
        TablesHeader = value?.Name is null
            ? "TABLES — All Schemas"
            : $"TABLES — {value.Name}";
        OnPropertyChanged(nameof(ShowSchemaColumn));
        RefreshVisibleTables();
    }

    partial void OnSelectedTableChanged(TableSelectionItem? value) => UpdateRelationships(value);

    partial void OnSelectedPresetChanged(PresetOption? value)
    {
        if (value is null || _model is null) return;
        if (value.Id == _loadedPresetId) return;

        _ = SwitchLoadedPresetAsync(value);
    }

    // ── Loading / building ─────────────────────────────────────────────────────

    private void ApplyActiveSelection()
    {
        var exclusions = _selectionService.ActiveSpec.ExcludedTables;

        _suspendCascadeDeselect = true;
        try
        {
            foreach (var item in _allItems)
            {
                item.IsSelected = !exclusions.Contains(item.Id);
            }
        }
        finally
        {
            _suspendCascadeDeselect = false;
        }

        SetLoadedPreset(
            _selectionService.ActivePresetId,
            [.. exclusions.Where(id => _itemsById.ContainsKey(id))]);

        RefreshAllDependencyWarnings();
    }

    private void BuildItems()
    {
        _allItems.Clear();
        _itemsById.Clear();

        if (_model is null) return;

        foreach (var table in _model.Tables)
        {
            var item = new TableSelectionItem(new TableId(table.SchemaName, table.Name));
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TableSelectionItem.IsSelected) && !_suspendItemRecount)
                {
                    if (!_suspendCascadeDeselect)
                    {
                        if (!item.IsSelected)
                        {
                            _ = ConfirmAndCascadeDeselectAsync(item);
                        }
                        else
                        {
                            // Re-selected manually — check if parents are excluded.
                            UpdateDependencyWarning(item);
                        }
                    }

                    UpdateCounts();
                }
            };

            _allItems.Add(item);
            _itemsById[item.Id] = item;
        }
    }

    private void BuildPresetOptions()
    {
        var keepId = _loadedPresetId;

        PresetOptions.Clear();
        PresetOptions.Add(PresetOption.AllTables);

        foreach (var preset in _selectionService.Presets)
        {
            PresetOptions.Add(new PresetOption(preset.Id, preset.Name));
        }

        // Reselecting the loaded preset raises no switch — OnSelectedPresetChanged
        // short-circuits when the id matches.
        SelectedPreset = PresetOptions.FirstOrDefault(o => o.Id == keepId)
            ?? PresetOption.AllTables;
    }

    private void BuildSchemaNodes()
    {
        SchemaNodes.Clear();

        if (_model is null) return;

        var schemaCount = _model.Tables
            .Select(t => t.SchemaName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var all = new SchemaNode(null, schemaCount);
        all.PropertyChanged += (_, e) => OnSchemaNodeToggled(all, e.PropertyName);
        SchemaNodes.Add(all);

        foreach (var schema in _model.Tables
                     .GroupBy(t => t.SchemaName, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var node = new SchemaNode(schema.Key, schema.Count());
            node.PropertyChanged += (_, e) => OnSchemaNodeToggled(node, e.PropertyName);
            SchemaNodes.Add(node);
        }

        SelectedSchemaNode = SchemaNodes.FirstOrDefault();
    }

    private async Task LoadSizesAsync()
    {
        try
        {
            var sizes = await _dbService.GetTableSizesAsync(_sourceConnection, CancellationToken.None);

            foreach (var size in sizes)
            {
                if (_itemsById.TryGetValue(size.Table, out var item))
                {
                    item.SizeBytes = size.SizeBytes;
                }
            }

            // Sizes may change the active sort order.
            if (_sortColumn == "Size")
            {
                RefreshVisibleTables();
            }
        }
        catch (Exception ex)
        {
            // Sizes are advisory — blanks sort last and never block the dialog.
            Log.Warning(ex, "[TableSelectionDialog] Table size load failed");
        }
    }

    // ── Selection actions ──────────────────────────────────────────────────────

    private void SetAllSelected(bool selected)
    {
        _suspendItemRecount = true;
        _suspendCascadeDeselect = true;
        try
        {
            foreach (var item in _allItems)
            {
                item.IsSelected = selected;
            }
        }
        finally
        {
            _suspendItemRecount = false;
            _suspendCascadeDeselect = false;
        }

        RefreshAllDependencyWarnings();
        UpdateCounts();
    }

    private async Task SwitchLoadedPresetAsync(PresetOption requested)
    {
        try
        {
            if (IsDialogDirty)
            {
                var discard = await _dialogService.ConfirmAsync(
                    "Unsaved Changes",
                    "Switching presets discards the current unsaved modifications. Continue?");

                if (!discard)
                {
                    RevertPresetSelection();
                    return;
                }
            }

            IReadOnlySet<TableId> exclusions;

            if (requested.Id is null)
            {
                exclusions = new HashSet<TableId>();
            }
            else
            {
                var preset = _selectionService.Presets.FirstOrDefault(p => p.Id == requested.Id);
                exclusions = preset?.ExcludedTables ?? new HashSet<TableId>();
            }

            _suspendItemRecount = true;
            _suspendCascadeDeselect = true;
            try
            {
                foreach (var item in _allItems)
                {
                    item.IsSelected = !exclusions.Contains(item.Id);
                }
            }
            finally
            {
                _suspendItemRecount = false;
                _suspendCascadeDeselect = false;
            }

            SetLoadedPreset(requested.Id, [.. exclusions.Where(id => _itemsById.ContainsKey(id))]);

            RefreshAllDependencyWarnings();
            UpdateCounts();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "[TableSelectionDialog] Failed to switch preset");
            RevertPresetSelection();
        }
    }

    private void SetLoadedPreset(string? presetId, HashSet<TableId> exclusions)
    {
        _loadedPresetId = presetId;
        _loadedExclusions = exclusions;
        OnPropertyChanged(nameof(IsNamedPresetLoaded));
        OnPropertyChanged(nameof(IsAllTablesLoaded));
    }

    private void RevertPresetSelection()
    {
        _suspendSchemaSync = true;
        try
        {
            SelectedPreset = PresetOptions.FirstOrDefault(o => o.Id == _loadedPresetId)
                ?? PresetOption.AllTables;
        }
        finally
        {
            _suspendSchemaSync = false;
        }
    }

    // ── Counts, filtering, sorting ─────────────────────────────────────────────

    private void UpdateCounts()
    {
        var selected = SelectedCount;
        SelectedCountText = $"Selected: {selected} / {_allItems.Count}";

        // Checkbox state feeds the "(selected)"/"(excluded)" relationship markers,
        // so the indicator follows every count update — including bulk toggles,
        // which run with per-item recount suspended.
        UpdateRelationships(SelectedTable);

        _suspendSchemaSync = true;
        try
        {
            var fullySelectedSchemas = 0;

            foreach (var node in SchemaNodes)
            {
                if (node.Name is null) continue; // handle "All Schemas" after per-schema nodes

                var scope = _allItems.Where(i =>
                    string.Equals(i.Schema, node.Name, StringComparison.OrdinalIgnoreCase));

                var nodeSelected = scope.Count(i => i.IsSelected);
                var total = scope.Count();

                node.SelectedCount = nodeSelected;
                node.IsChecked = nodeSelected == 0
                    ? false
                    : nodeSelected == total ? true : null;

                if (nodeSelected == total)
                    fullySelectedSchemas++;
            }

            // "All Schemas" node shows how many schemas are fully selected
            var allNode = SchemaNodes.FirstOrDefault(n => n.Name is null);
            if (allNode is not null)
            {
                allNode.SelectedCount = fullySelectedSchemas;
                var totalSchemas = SchemaNodes.Count - 1; // exclude "All Schemas" itself
                allNode.IsChecked = fullySelectedSchemas == 0
                    ? false
                    : fullySelectedSchemas == totalSchemas ? true : null;
            }
        }
        finally
        {
            _suspendSchemaSync = false;
        }
    }

    private void OnSchemaNodeToggled(SchemaNode node, string? propertyName)
    {
        if (propertyName != nameof(SchemaNode.IsChecked) || _suspendSchemaSync) return;

        var select = node.IsChecked == true;

        _suspendItemRecount = true;
        _suspendCascadeDeselect = true;
        try
        {
            foreach (var item in _allItems)
            {
                if (node.Name is null ||
                    string.Equals(item.Schema, node.Name, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsSelected = select;
                }
            }
        }
        finally
        {
            _suspendItemRecount = false;
            _suspendCascadeDeselect = false;
        }

        RefreshAllDependencyWarnings();
        UpdateCounts();
    }

    private void RefreshVisibleTables()
    {
        VisibleTables.Clear();

        var schemaFilter = SelectedSchemaNode?.Name;
        var search = SearchText.Trim();

        IEnumerable<TableSelectionItem> query = _allItems;

        if (schemaFilter is not null)
        {
            query = query.Where(i =>
                string.Equals(i.Schema, schemaFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (search.Length > 0)
        {
            query = query.Where(i =>
                i.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        query = _sortColumn switch
        {
            "Name" => _sortDescending
                ? query.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase)
                : query.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "Size" => _sortDescending
                ? query.OrderByDescending(i => i.SizeBytes ?? -1)
                : query.OrderBy(i => i.SizeBytes is null).ThenBy(i => i.SizeBytes ?? 0),
            _ => _sortDescending
                ? query.OrderByDescending(i => i.Schema, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase)
                : query.OrderBy(i => i.Schema, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
        };

        foreach (var item in query)
        {
            VisibleTables.Add(item);
        }
    }

    // ── FK cascade deselect & dependency warnings ────────────────────────────

    /// <summary>
    /// Collects all currently-selected tables that transitively depend on <paramref name="parentId"/>
    /// via foreign keys (recursive). Used to build the confirmation message.
    /// </summary>
    private List<TableId> GetSelectedDependents(TableId parentId)
    {
        if (_model is null) return [];

        var result = new List<TableId>();
        var visited = new HashSet<TableId> { parentId };
        var queue = new Queue<TableId>();
        queue.Enqueue(parentId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            var dependents = _model.Tables
                .Where(t => t.ForeignKeys.Any(fk =>
                    string.Equals(fk.ReferencedSchema, current.Schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(fk.ReferencedTable, current.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(t => new TableId(t.SchemaName, t.Name))
                .Where(id => id != parentId && visited.Add(id))
                .ToList();

            foreach (var depId in dependents)
            {
                if (_itemsById.TryGetValue(depId, out var depItem) && depItem.IsSelected)
                {
                    result.Add(depId);
                    queue.Enqueue(depId);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Shows a confirmation dialog listing dependent tables that will be
    /// auto-deselected. If confirmed (or no dependents), cascades. If cancelled,
    /// reverts the uncheck.
    /// </summary>
    private async Task ConfirmAndCascadeDeselectAsync(TableSelectionItem uncheckedItem)
    {
        var dependents = GetSelectedDependents(uncheckedItem.Id);

        if (dependents.Count > 0)
        {
            var tableList = string.Join("\n", dependents.Select(d => $"  • {Display(d)}"));
            var message = $"The following tables depend on {Display(uncheckedItem.Id)} " +
                $"and will also be deselected:\n\n{tableList}\n\nProceed?";

            var confirmed = await _dialogService.ConfirmAsync("Deselect Dependent Tables", message);

            if (!confirmed)
            {
                // Revert the uncheck — suppress cascade/recount to avoid recursion.
                _suspendCascadeDeselect = true;
                _suspendItemRecount = true;
                try
                {
                    uncheckedItem.IsSelected = true;
                }
                finally
                {
                    _suspendCascadeDeselect = false;
                    _suspendItemRecount = false;
                }

                UpdateCounts();
                return;
            }
        }

        CascadeDeselect(uncheckedItem.Id);
        RefreshAllDependencyWarnings();
        UpdateCounts();
    }

    /// <summary>
    /// When a table is deselected, automatically deselect all tables that have
    /// a FK referencing it (children cannot exist without their parent).
    /// Operates recursively — if deselecting A causes B to deselect, B's dependents
    /// are also deselected. Suppresses further confirmation dialogs during cascade.
    /// </summary>
    private void CascadeDeselect(TableId deselectedId)
    {
        if (_model is null) return;

        _suspendCascadeDeselect = true;
        _suspendItemRecount = true;
        try
        {
            var visited = new HashSet<TableId> { deselectedId };
            var queue = new Queue<TableId>();
            queue.Enqueue(deselectedId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                var dependents = _model.Tables
                    .Where(t => t.ForeignKeys.Any(fk =>
                        string.Equals(fk.ReferencedSchema, current.Schema, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(fk.ReferencedTable, current.Name, StringComparison.OrdinalIgnoreCase)))
                    .Select(t => new TableId(t.SchemaName, t.Name))
                    .Where(id => id != deselectedId && visited.Add(id))
                    .ToList();

                foreach (var depId in dependents)
                {
                    if (_itemsById.TryGetValue(depId, out var depItem) && depItem.IsSelected)
                    {
                        depItem.IsSelected = false;
                        queue.Enqueue(depId);
                    }
                }
            }
        }
        finally
        {
            _suspendCascadeDeselect = false;
            _suspendItemRecount = false;
        }

        // After cascade, clear warning on the deselected table itself (it's excluded).
        if (_itemsById.TryGetValue(deselectedId, out var item))
        {
            item.HasDependencyWarning = false;
        }
    }

    /// <summary>
    /// Sets <see cref="TableSelectionItem.HasDependencyWarning"/> on a single item
    /// based on whether any of its FK-referenced parent tables are currently excluded.
    /// </summary>
    private void UpdateDependencyWarning(TableSelectionItem item)
    {
        if (_model is null)
        {
            item.HasDependencyWarning = false;
            return;
        }

        if (!item.IsSelected)
        {
            item.HasDependencyWarning = false;
            return;
        }

        var tableDef = _model.Tables.FirstOrDefault(t =>
            string.Equals(t.SchemaName, item.Id.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Name, item.Id.Name, StringComparison.OrdinalIgnoreCase));

        if (tableDef is null)
        {
            item.HasDependencyWarning = false;
            return;
        }

        var hasExcludedParent = tableDef.ForeignKeys.Any(fk =>
        {
            var parentId = new TableId(fk.ReferencedSchema, fk.ReferencedTable);
            if (parentId == item.Id) return false; // self-referencing FK
            return _itemsById.TryGetValue(parentId, out var parentItem) && !parentItem.IsSelected;
        });

        item.HasDependencyWarning = hasExcludedParent;
    }

    /// <summary>
    /// Recomputes the dependency warning on all selected items. Called after bulk
    /// operations (Select All, Select None, preset switch) where cascade was suspended.
    /// </summary>
    private void RefreshAllDependencyWarnings()
    {
        foreach (var item in _allItems)
        {
            UpdateDependencyWarning(item);
        }
    }

    // ── Relationship explorer (FR10) ───────────────────────────────────────────

    private void UpdateRelationships(TableSelectionItem? selected)
    {
        foreach (var item in _allItems)
        {
            if (item.IsRelated)
            {
                item.IsRelated = false;
                item.RelationshipIndicator = string.Empty;
            }

            // The previously highlighted row carries the summary tooltip
            // without being marked as related — clear it separately.
            if (item.RelationshipTooltip is not null)
            {
                item.RelationshipTooltip = null;
            }
        }

        if (selected is null || _model is null)
        {
            RelationshipDetails = string.Empty;
            return;
        }

        var id = selected.Id;

        // Tables this table points to via its own foreign keys.
        var tableDef = _model.Tables.FirstOrDefault(t =>
            string.Equals(t.SchemaName, id.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Name, id.Name, StringComparison.OrdinalIgnoreCase));

        var references = (tableDef?.ForeignKeys ?? [])
            .Select(fk => new TableId(fk.ReferencedSchema, fk.ReferencedTable))
            .Distinct()
            .ToList();

        // Tables that hold a foreign key pointing at the selected table.
        var referencedBy = _model.Tables
            .Where(t => t.ForeignKeys.Any(fk =>
                string.Equals(fk.ReferencedSchema, id.Schema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(fk.ReferencedTable, id.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(t => new TableId(t.SchemaName, t.Name))
            .ToList();

        foreach (var related in references.Concat(referencedBy))
        {
            if (related == id) continue; // self-referencing FK — not a separate row

            if (_itemsById.TryGetValue(related, out var item))
            {
                item.IsRelated = true;

                var isParent = references.Contains(related);
                var arrow = isParent ? "⬆️" : "⬇️";
                var suffix = item.IsSelected ? "" : " (excluded)";
                item.RelationshipIndicator = $" {arrow}{suffix}";
                item.RelationshipTooltip = isParent
                    ? $"Parent of {Display(id)}"
                    : $"Child of {Display(id)}";
            }
        }

        RelationshipDetails = references.Count == 0 && referencedBy.Count == 0
            ? $"{Display(id)}: no foreign-key relationships"
            : $"{Display(id)} — References: {Describe(references)} · Referenced by: {Describe(referencedBy)}";

        // The highlighted row itself gets the full summary as its tooltip.
        selected.RelationshipTooltip = RelationshipDetails;
    }

    /// <summary>
    /// Table name for dialog messages and tooltips: the bare name while a single
    /// schema is in view, schema-qualified in the "All Schemas" view or when the
    /// table belongs to a different schema (cross-schema relationships).
    /// </summary>
    private string Display(TableId id) =>
        SelectedSchemaNode?.Name is { } schema
            && string.Equals(id.Schema, schema, StringComparison.OrdinalIgnoreCase)
            ? id.Name
            : id.FullName;

    private string Describe(IReadOnlyList<TableId> tables) =>
        tables.Count == 0
            ? "—"
            : string.Join(
                ", ",
                tables.Select(t =>
                    _itemsById.TryGetValue(t, out var item)
                        ? item.IsSelected ? Display(t) : $"{Display(t)} (excluded)"
                        : Display(t)));
}
