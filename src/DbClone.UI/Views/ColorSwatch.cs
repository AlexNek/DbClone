using System.Windows;
using System.Windows.Controls;

namespace DbClone.UI.Views;

/// <summary>
/// Reusable colour swatch (rounded rectangle). Used in colour picker buttons.
/// </summary>
public partial class ColorSwatch : UserControl
{
    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(
            nameof(Color),
            typeof(string),
            typeof(ColorSwatch),
            new PropertyMetadata(null));

    /// <summary>Hex colour string (e.g. "#FF0000"), or null for no colour.</summary>
    public string? Color
    {
        get => (string?)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(
            nameof(Size),
            typeof(double),
            typeof(ColorSwatch),
            new PropertyMetadata(22.0));

    /// <summary>Side length of the square swatch in pixels.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public ColorSwatch()
    {
        InitializeComponent();
    }
}
