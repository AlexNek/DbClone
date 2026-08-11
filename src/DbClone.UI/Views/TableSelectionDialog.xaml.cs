using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

using DbClone.UI.ViewModels;

namespace DbClone.UI.Views;

/// <summary>
/// Master-detail table selection dialog with preset management.
/// Interactive prompts live in the view layer; the view model exposes pure state.
/// </summary>
public partial class TableSelectionDialog : Window
{
    private readonly CancellationTokenSource _cts = new();

    private readonly TableSelectionViewModel _vm;

    private bool _committed;

    /// <summary>Initializes a new instance. Metadata loading starts when the window loads.</summary>
    public TableSelectionDialog(TableSelectionViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        TablesListView.View = ResolveTableView();
        Loaded += OnLoadedAsync;
    }

    private async void ApplyAnywayClick(object sender, RoutedEventArgs e) =>
        await CommitAndCloseAsync();

    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsSelectionEmpty)
        {
            MessageBox.Show(
                this,
                "At least one table must be selected when the database contains tables.",
                "Empty Selection",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // FR17 — a modified selection on "All Tables" suggests saving it first.
        if (_vm.IsAllTablesLoaded && _vm.IsDialogDirty)
        {
            var answer = MessageBox.Show(
                this,
                "You have a custom selection. Save it as a named selection?",
                "Save Selection",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return;

            if (answer == MessageBoxResult.Yes && !await PromptSaveAsAsync()) return;
        }

        if (_vm.HasValidationIssues())
        {
            _vm.ShowValidationSummary = true;
            return;
        }

        await CommitAndCloseAsync();
    }

    private async void CancelClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync()) return;

        DialogResult = false;
        Close();
    }

    private async void DeleteClick(object sender, RoutedEventArgs e) =>
        await _vm.DeletePresetAsync();

    private void GoBackClick(object sender, RoutedEventArgs e) =>
        _vm.HideValidationSummaryCommand.Execute(null);

    private async void RenameClick(object sender, RoutedEventArgs e)
    {
        var dialog = new PresetNameDialog
        {
            Owner = this,
            Title = "Rename Preset",
            InitialName = _vm.LoadedPresetName ?? string.Empty
        };

        if (dialog.ShowDialog() != true) return;

        var error = await _vm.RenamePresetAsync(dialog.PresetName);

        if (error is not null)
        {
            MessageBox.Show(this, error, "Rename Preset", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SaveAsClick(object sender, RoutedEventArgs e) =>
        await PromptSaveAsAsync();

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        var error = await _vm.SavePresetAsync();

        if (error is not null)
        {
            MessageBox.Show(this, error, "Save Preset", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        _cts.Cancel();

        // Closing via the X button — prompt before discarding unsaved modifications.
        if (!_committed && DialogResult is null && _vm.IsDialogDirty)
        {
            var discard = MessageBox.Show(
                this,
                "You have unsaved changes. Discard?",
                "Unsaved Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (discard != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
    }

    // ── Private ────────────────────────────────────────────────────────────

    private async Task CommitAndCloseAsync()
    {
        await _vm.CommitAsync();
        _committed = true;
        DialogResult = true;
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_vm.IsDialogDirty) return true;

        var discard = MessageBox.Show(
            this,
            "You have unsaved changes. Discard?",
            "Unsaved Changes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        return await Task.FromResult(discard == MessageBoxResult.Yes);
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e) =>
        await _vm.LoadAsync(_cts.Token);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableSelectionViewModel.ShowSchemaColumn))
        {
            TablesListView.View = ResolveTableView();
        }
    }

    /// <summary>
    /// Picks the table grid layout for the current schema scope. Assigned in
    /// code-behind because a style trigger re-evaluating a shared GridView
    /// resource throws "View can't be shared by more than one ListView".
    /// </summary>
    private GridView ResolveTableView() =>
        _vm.ShowSchemaColumn
            ? (GridView)FindResource("TableGridAllSchemas")
            : (GridView)FindResource("TableGridSingleSchema");

    /// <summary>Save As… loop: keeps asking until the name validates or the user gives up.</summary>
    private async Task<bool> PromptSaveAsAsync()
    {
        while (true)
        {
            var dialog = new PresetNameDialog
            {
                Owner = this,
                Title = "Save Table Selection",
                Disclosure = "Presets store excluded tables — tables added to the database later "
                    + "are included automatically unless you exclude them."
            };

            if (dialog.ShowDialog() != true) return false;

            var error = await _vm.SaveAsPresetAsync(dialog.PresetName);
            if (error is null) return true;

            var retry = MessageBox.Show(
                this,
                $"{error}\n\nTry a different name?",
                "Save Table Selection",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (retry != MessageBoxResult.Yes) return false;
        }
    }
}
