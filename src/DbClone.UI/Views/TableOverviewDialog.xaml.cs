using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

using DbClone.UI.ViewModels;

namespace DbClone.UI.Views;

/// <summary>
/// Read-only table overview dialog for the destination database.
/// Shows all tables with search and sorting. Includes a database selector
/// at the top so the user can browse tables on any accessible database.
/// </summary>
public partial class TableOverviewDialog : Window
{
    private readonly CancellationTokenSource _cts = new();

    private readonly TableOverviewViewModel _vm;

    /// <summary>Initializes a new instance. Metadata loading starts when the window loads.</summary>
    public TableOverviewDialog(TableOverviewViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        TablesListView.View = ResolveTableView();
        Loaded += OnLoadedAsync;
    }

    /// <summary>True when the user changed the database inside the dialog.</summary>
    public bool DatabaseChanged => _vm.DatabaseChanged;

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        _cts.Cancel();
        base.OnClosing(e);
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.LoadAsync(_cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableOverviewViewModel.ShowSchemaColumn))
        {
            TablesListView.View = ResolveTableView();
        }
    }

    /// <summary>
    /// Picks the table grid layout for the current schema scope.
    /// </summary>
    private GridView ResolveTableView() =>
        _vm.ShowSchemaColumn
            ? (GridView)FindResource("OverviewGridAllSchemas")
            : (GridView)FindResource("OverviewGridSingleSchema");
}
