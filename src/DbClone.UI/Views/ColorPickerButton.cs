using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DbClone.UI.Views;

/// <summary>
/// Colour picker trigger button: swatch + chevron affordance.
/// Opens a colour picker dialog when clicked (via Command).
/// </summary>
public partial class ColorPickerButton : UserControl
{
    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(
            nameof(Color),
            typeof(string),
            typeof(ColorPickerButton),
            new PropertyMetadata(null));

    /// <summary>Currently selected hex colour, or null.</summary>
    public string? Color
    {
        get => (string?)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(
            nameof(Command),
            typeof(ICommand),
            typeof(ColorPickerButton),
            new PropertyMetadata(null));

    /// <summary>Command executed on click (opens the colour picker dialog).</summary>
    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(
            nameof(Size),
            typeof(double),
            typeof(ColorPickerButton),
            new PropertyMetadata(22.0));

    /// <summary>Swatch size in pixels.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public ColorPickerButton()
    {
        InitializeComponent();
    }
}
