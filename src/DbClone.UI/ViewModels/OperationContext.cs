using CommunityToolkit.Mvvm.ComponentModel;

namespace DbClone.UI.ViewModels;

/// <summary>
/// Shared coordination state between child ViewModels.
/// Holds only cross-workflow concerns: connections and the global busy lock.
/// Per-workflow state (logs, banner, status, objects) lives in <see cref="WorkflowState"/>.
/// </summary>
public sealed partial class OperationContext : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Destination connection.</summary>
    public ConnectionViewModel Destination { get; }

    /// <summary>Source connection.</summary>
    public ConnectionViewModel Source { get; }

    public OperationContext(ConnectionViewModel source, ConnectionViewModel destination)
    {
        Source = source;
        Destination = destination;
    }
}
