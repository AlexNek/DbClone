using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

using DbClone.UI.ViewModels;

namespace DbClone.UI.Views;

/// <summary>
/// Colour swatch grid built on a <see cref="Grid"/> panel — the proper 2D layout
/// control. The separator between harmonic rows and pure primaries is an empty
/// grid row with its own height, so no cell overflow or clipping can occur.
/// </summary>
public partial class ColorPickerControl : UserControl
{
    private const double CellPadding = 2;

    private const int Columns = 8;

    private const int HarmonicRows = ColorPickerViewModel.HarmonicCount / Columns; // 6

    private const int SeparatorRow = HarmonicRows; // row index 6

    private const double SwatchSize = 24;

    private readonly List<(Border Border, string? Color)> _cells = [];

    private string? _currentColor;

    public static readonly DependencyProperty SelectedColorProperty =
        DependencyProperty.Register(
            nameof(SelectedColor),
            typeof(string),
            typeof(ColorPickerControl),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Currently selected hex colour, or null for "none".</summary>
    public string? SelectedColor
    {
        get => (string?)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public ColorPickerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // ── Grid construction ──────────────────────────────────────────────────────

    private void BuildGrid()
    {
        var colors = ColorPickerViewModel.s_presetColors;

        for (int c = 0; c < Columns; c++)
            SwatchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (int r = 0; r < HarmonicRows; r++)
            SwatchGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Separator row — empty row with a horizontal line to visually divide sections.
        SwatchGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });

        // Add a separator line spanning all columns.
        var separator = new Border
                            {
                                Height = 1,
                                Background =
                                    TryResource("ControlStrokeColorDefaultBrush") as Brush
                                    ?? Brushes.LightGray,
                                Margin = new Thickness(2, 5, 2, 5),
                                VerticalAlignment = VerticalAlignment.Center
                            };
        Grid.SetRow(separator, SeparatorRow);
        Grid.SetColumnSpan(separator, Columns);
        SwatchGrid.Children.Add(separator);

        // Pure primaries row.
        SwatchGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < colors.Length; i++)
        {
            int col = i % Columns;
            int dataRow = i / Columns;
            int gridRow = dataRow < HarmonicRows ? dataRow : dataRow + 1; // skip separator

            var swatch = CreateSwatch(colors[i]);
            Grid.SetRow(swatch, gridRow);
            Grid.SetColumn(swatch, col);
            SwatchGrid.Children.Add(swatch);
            _cells.Add((swatch, colors[i]));
        }
    }

    private Border CreateSwatch(string? color)
    {
        var rect = new Rectangle
                       {
                           Width = SwatchSize,
                           Height = SwatchSize,
                           RadiusX = 3,
                           RadiusY = 3,
                           StrokeThickness = 1,
                       };

        if (color is not null)
        {
            rect.Fill = HexToBrush(color);
            rect.Stroke = TryResource("ControlStrokeColorDefaultBrush") as Brush ?? Brushes.Gray;
        }
        else
        {
            rect.Fill = Brushes.White;
            rect.Stroke = TryResource("ControlStrokeColorDefaultBrush") as Brush ?? Brushes.Gray;
        }

        var inner = new Grid { Width = SwatchSize, Height = SwatchSize };
        inner.Children.Add(rect);

        if (color is null)
        {
            inner.Children.Add(
                new Line
                    {
                        X1 = 4,
                        Y1 = 4,
                        X2 = 20,
                        Y2 = 20,
                        Stroke = Brushes.Gray,
                        StrokeThickness = 1.5,
                    });
        }

        var border = new Border
                         {
                             Padding = new Thickness(2),
                             Margin = new Thickness(CellPadding),
                             Background = Brushes.Transparent,
                             BorderBrush = Brushes.Transparent,
                             BorderThickness = new Thickness(2),
                             CornerRadius = new CornerRadius(5),
                             Cursor = Cursors.Hand,
                             Child = inner,
                             ToolTip = color,
                         };

        border.MouseLeftButtonUp += SwatchClick;
        border.MouseEnter += (_, _) =>
            {
                if (!IsSelected(border))
                    border.Background = TryResource("ControlFillColorDefaultBrush") as Brush
                                        ?? Brushes.Transparent;
            };
        border.MouseLeave += (_, _) =>
            {
                if (!IsSelected(border))
                    border.Background = Brushes.Transparent;
            };

        return border;
    }

    private static Brush HexToBrush(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(c);
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    private bool IsSelected(Border border) =>
        _cells.FirstOrDefault(c => c.Border == border).Color is { } c
        && string.Equals(
            NormalizeHex(c),
            NormalizeHex(_currentColor),
            StringComparison.OrdinalIgnoreCase);

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Strips leading '#' so that "0000ff" and "#0000FF" compare as equal.</summary>
    private static string? NormalizeHex(string? hex) => hex?.TrimStart('#');

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        BuildGrid();

        // Read the initial colour from the VM — the DP binding may not
        // have delivered it yet, but the VM always has the correct value.
        var initial = (DataContext as ColorPickerViewModel)?.SelectedColor;
        SetSelected(initial);
    }

    private void SetSelected(string? color)
    {
        _currentColor = color;
        var normalized = NormalizeHex(color);
        foreach (var (border, c) in _cells)
        {
            bool selected = string.Equals(
                NormalizeHex(c),
                normalized,
                StringComparison.OrdinalIgnoreCase);
            border.BorderBrush = selected
                                     ? TryResource("AccentTextFillColorPrimaryBrush") as Brush
                                       ?? Brushes.DodgerBlue
                                     : Brushes.Transparent;
            border.Background = Brushes.Transparent;
        }
    }

    // ── Selection ──────────────────────────────────────────────────────────────

    private void SwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border) return;
        var color = _cells.First(c => c.Border == border).Color;
        SetSelected(color);
        SelectedColor = color;
        // Push directly to the VM so the dialog result is always correct.
        if (DataContext is ColorPickerViewModel vm)
            vm.SelectedColor = color;
    }

    private static object? TryResource(string key) =>
        System.Windows.Application.Current.TryFindResource(key);
}
