using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.Models;
using DbClone.UI.Models;
using DbClone.UI.Services;

using Serilog;

namespace DbClone.UI.ViewModels;

/// <summary>
/// View model for the "Tables:" row on the source connection panel:
/// preset dropdown, Edit… entry point, dirty indicator and optional counts.
/// Delegates all selection state to <see cref="ITableSelectionService"/>.
/// </summary>
public sealed partial class TableSelectionPanelViewModel : ObservableObject
{
    private readonly IDialogService _dialogService;

    private readonly IDatabaseService _dbService;

    private readonly ITableSelectionService _selectionService;

    private readonly ConnectionViewModel _sourceConnection;

    private bool _suspendSelectionSync;

    [ObservableProperty]
    private string? _countText;

    [ObservableProperty]
    private string _displayLabel = "All Tables";

    [ObservableProperty]
    private bool _hasConnection;

    [ObservableProperty]
    private PresetOption? _selectedOption;

    [ObservableProperty]
    private string? _statusNote;

    /// <summary>Raised when the user clicks Edit… — the host opens the dialog.</summary>
    public event EventHandler? EditRequested;

    /// <summary>Dropdown entries: "All Tables" + saved presets.</summary>
    public ObservableCollection<PresetOption> Options { get; } = [];

    /// <summary>Initializes a new instance.</summary>
    public TableSelectionPanelViewModel(
        ITableSelectionService selectionService,
        IDialogService dialogService,
        IDatabaseService dbService,
        ConnectionViewModel sourceConnection)
    {
        _selectionService = selectionService;
        _dialogService = dialogService;
        _dbService = dbService;
        _sourceConnection = sourceConnection;
        _selectionService.Changed += RefreshFromService;
        RefreshFromService();
    }

    /// <summary>Opens the table selection dialog.</summary>
    [RelayCommand]
    private void EditTables() => EditRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Loads presets for the given connection and restores the last-used preset.
    /// Kicks off a background table count load — the panel never blocks on it.
    /// </summary>
    public async Task LoadForConnectionAsync(SavedConnection? connection)
    {
        await _selectionService.LoadForConnectionAsync(connection);
        HasConnection = connection is not null;

        if (connection is null)
        {
            CountText = null;
            StatusNote = null;
            return;
        }

        _ = LoadCountsAsync();
    }

    // ── Private ────────────────────────────────────────────────────────────────

    private async Task LoadCountsAsync()
    {
        try
        {
            var tables = await _dbService.GetTablesAsync(_sourceConnection, CancellationToken.None);
            var existing = new HashSet<TableId>(tables.Select(t => new TableId(t.Schema, t.Name)));
            var excluded = _selectionService.ActiveSpec.ExcludedTables.Count(id => existing.Contains(id));

            CountText = $"{tables.Count - excluded}/{tables.Count}";
            StatusNote = null;
        }
        catch (Exception ex)
        {
            // Counts are optional — a failed background load only hides the count
            // and surfaces a warning note; it never blocks the panel.
            Log.Warning(ex, "[TableSelectionPanel] Background table count load failed");
            CountText = null;
            StatusNote = "Table count unavailable — connection check failed";
        }
    }

    partial void OnSelectedOptionChanged(PresetOption? value)
    {
        if (_suspendSelectionSync || value is null) return;
        if (value.Id == _selectionService.ActivePresetId) return;

        // Switching presets discards an unsaved temporary selection — prompt first.
        if (_selectionService.IsDirty)
        {
            var requested = value;
            SetSelectedOptionSilently(OptionForActivePreset());
            _ = PromptAndSwitchAsync(requested);
            return;
        }

        _ = _selectionService.SetActivePresetAsync(value.Id);
    }

    private PresetOption OptionForActivePreset() =>
        Options.FirstOrDefault(o => o.Id == _selectionService.ActivePresetId)
        ?? PresetOption.AllTables;

    private async Task PromptAndSwitchAsync(PresetOption requested)
    {
        var discard = await _dialogService.ConfirmAsync(
            "Unsaved Table Selection",
            "The current table selection has unsaved modifications. "
            + "Discard them and switch presets?");

        if (!discard) return;

        await _selectionService.SetActivePresetAsync(requested.Id);
    }

    private void RefreshFromService()
    {
        _suspendSelectionSync = true;
        try
        {
            Options.Clear();
            Options.Add(PresetOption.AllTables);

            foreach (var preset in _selectionService.Presets)
            {
                Options.Add(new PresetOption(preset.Id, preset.Name));
            }

            SelectedOption = OptionForActivePreset();

            var presetName = SelectedOption?.Name ?? "All Tables";
            DisplayLabel = _selectionService.IsDirty ? $"{presetName} *" : presetName;
        }
        finally
        {
            _suspendSelectionSync = false;
        }
    }

    private void SetSelectedOptionSilently(PresetOption option)
    {
        _suspendSelectionSync = true;
        try
        {
            SelectedOption = option;
        }
        finally
        {
            _suspendSelectionSync = false;
        }
    }
}
