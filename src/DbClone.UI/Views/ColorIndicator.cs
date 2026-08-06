using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DbClone.UI.Views;

/// <summary>
/// Colour indicator: ellipse + optional text label with search highlighting.
/// Used in lists/ComboBoxes (with label) or standalone (dot only).
/// </summary>
public partial class ColorIndicator : UserControl
{
    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(
            nameof(Color),
            typeof(string),
            typeof(ColorIndicator),
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
            typeof(ColorIndicator),
            new PropertyMetadata(9.0));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(ColorIndicator),
            new PropertyMetadata(null, OnLabelOrHighlightChanged));

    public static readonly DependencyProperty HighlightTextProperty =
        DependencyProperty.Register(
            nameof(HighlightText),
            typeof(string),
            typeof(ColorIndicator),
            new PropertyMetadata(null, OnLabelOrHighlightChanged));

    private static readonly DependencyPropertyKey HasLabelPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasLabel),
            typeof(bool),
            typeof(ColorIndicator),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HasLabelProperty =
        HasLabelPropertyKey.DependencyProperty;

    /// <summary>True when Label is non-empty.</summary>
    public bool HasLabel => (bool)GetValue(HasLabelProperty);

    private static void OnLabelOrHighlightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ColorIndicator)d;
        var label = control.Label;
        control.SetValue(HasLabelPropertyKey, !string.IsNullOrEmpty(label));
        control.UpdateHighlightedInlines();
    }

    /// <summary>Optional text label displayed next to the ellipse.</summary>
    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Search text to highlight within the label. When null/empty, no highlighting is applied.</summary>
    public string? HighlightText
    {
        get => (string?)GetValue(HighlightTextProperty);
        set => SetValue(HighlightTextProperty, value);
    }

    /// <summary>Diameter in pixels.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public ColorIndicator()
    {
        InitializeComponent();
    }

    private void UpdateHighlightedInlines()
    {
        var labelTextBlock = LabelTextBlock;
        if (labelTextBlock is null) return;

        var label = Label;
        var highlight = HighlightText;

        labelTextBlock.Inlines.Clear();

        if (string.IsNullOrEmpty(label))
            return;

        if (string.IsNullOrEmpty(highlight))
        {
            labelTextBlock.Inlines.Add(new Run(label));
            return;
        }

        // Find all occurrences (case-insensitive) and highlight them
        var highlightBrush = (Brush)new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 255, 200, 0));
        int pos = 0;
        while (pos < label.Length)
        {
            int matchIndex = label.IndexOf(highlight, pos, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                // Remainder — no more matches
                labelTextBlock.Inlines.Add(new Run(label[pos..]));
                break;
            }

            // Text before the match
            if (matchIndex > pos)
                labelTextBlock.Inlines.Add(new Run(label[pos..matchIndex]));

            // The matched portion — highlighted
            labelTextBlock.Inlines.Add(new Run(label[matchIndex..(matchIndex + highlight.Length)])
            {
                Background = highlightBrush,
                FontWeight = FontWeights.SemiBold
            });

            pos = matchIndex + highlight.Length;
        }
    }
}
