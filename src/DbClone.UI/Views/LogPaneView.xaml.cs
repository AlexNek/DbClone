using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DbClone.UI.Views;

public partial class LogPaneView : UserControl
{
    private bool _scrollPending;

    public LogPaneView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // Subscribe to collection changes for auto-scroll
        if (DataContext is System.ComponentModel.INotifyPropertyChanged)
        {
            // Try to find the LogMessages collection via binding
            if (LogListBox.ItemsSource is System.Collections.IList list
                && list is INotifyCollectionChanged ncc)
            {
                ncc.CollectionChanged += (_, _) => AutoScrollToEnd();
            }
        }
    }

    private void AutoScrollToEnd()
    {
        if (_scrollPending) return;
        _scrollPending = true;

        Dispatcher.InvokeAsync(
            () =>
                {
                    _scrollPending = false;
                    if (LogListBox.Items.Count > 0)
                        LogListBox.ScrollIntoView(LogListBox.Items[^1]);
                },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Exposes the inner ListBox so the parent window can hook collection events if needed.
    /// </summary>
    public ListBox ListBox => LogListBox;
}
