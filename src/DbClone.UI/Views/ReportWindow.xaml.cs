using DbClone.UI.ViewModels;

using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DbClone.UI.Views;

/// <summary>
/// Report window showing formatted comparison results with export options.
/// </summary>
public partial class ReportWindow : FluentWindow
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReportWindow"/> class.
    /// </summary>
    public ReportWindow(CompareViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
