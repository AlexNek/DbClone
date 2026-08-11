using System.Windows;

using DbClone.UI.Models;

namespace DbClone.UI.Views;

/// <summary>
/// Custom dialog for choosing how the target database should be cleaned
/// when a table selection is active. Replaces the ambiguous Yes/No/Cancel
/// MessageBox with explicit action buttons.
/// </summary>
public partial class CleanTargetDialog : Window
{
    /// <summary>The user's choice.</summary>
    public ESelectionCleanChoice Choice { get; private set; } = ESelectionCleanChoice.Cancel;

    public CleanTargetDialog()
    {
        InitializeComponent();
    }

    private void ClearAllClick(object sender, RoutedEventArgs e)
    {
        Choice = ESelectionCleanChoice.ClearEntireDestination;
        DialogResult = true;
        Close();
    }

    private void ReplaceSelectedClick(object sender, RoutedEventArgs e)
    {
        Choice = ESelectionCleanChoice.ReplaceSelectedOnly;
        DialogResult = true;
        Close();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        Choice = ESelectionCleanChoice.Cancel;
        DialogResult = false;
        Close();
    }
}
