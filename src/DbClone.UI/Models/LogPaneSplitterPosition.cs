namespace DbClone.UI.Models;

/// <summary>
/// Remembered log pane splitter position for a single workflow.
/// The main window keeps two independent instances (Copy, Compare) so each
/// mode restores its own dragged height. Pure state — no WPF dependencies.
/// </summary>
public sealed class LogPaneSplitterPosition
{
    /// <summary>Default height (px) used when no valid saved value exists.</summary>
    public const double DefaultHeight = 200;

    private double _height = DefaultHeight;

    /// <summary>The remembered height in pixels.</summary>
    public double Height => _height;

    /// <summary>
    /// Remembers the live dragged height. Collapsed (zero or negative) values
    /// are ignored so hiding the pane never wipes the remembered position.
    /// </summary>
    public void Capture(double liveHeight)
    {
        if (liveHeight > 0)
            _height = liveHeight;
    }

    /// <summary>Restores a persisted height, falling back to the default for invalid values.</summary>
    public void Restore(double savedHeight) =>
        _height = savedHeight is > 100 and < 2000 ? savedHeight : DefaultHeight;
}
