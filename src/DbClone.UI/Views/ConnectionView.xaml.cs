using System.Windows.Controls;

namespace DbClone.UI.Views;

/// <summary>
/// Reusable view for selecting a saved database connection on the main window.
/// All field editing is done through the Connection Manager.
/// </summary>
public partial class ConnectionView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionView"/> class.
    /// </summary>
    public ConnectionView()
    {
        InitializeComponent();
    }
}
