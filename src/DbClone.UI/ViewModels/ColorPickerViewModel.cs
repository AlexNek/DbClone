using CommunityToolkit.Mvvm.ComponentModel;

namespace DbClone.UI.ViewModels;

/// <summary>
/// ViewModel for the colour picker dialog. Holds the palette and current selection.
/// </summary>
public partial class ColorPickerViewModel : ObservableObject
{
    /// <summary>Number of harmonic colour rows (before the pure primaries).</summary>
    internal const int HarmonicCount = 48;

    /// <summary>Shared immutable palette data.</summary>
    internal static readonly string?[] s_presetColors =
        [
            // Row 1 – L=84% (+ none)
            null, "#F4D6D6", "#F4E4D6", "#F4F4D6", "#D6F4D6", "#D6F4F4", "#D6E3F4", "#E9D6F4",
            // Row 2 – L=70%
            "#FFFFFF", "#ECAFAF", "#ECCFAF", "#ECECAF", "#AFECAF", "#AFECEC", "#AFC7EC", "#D3AFEC",
            // Row 3 – L=56%
            "#C0C0C0", "#E38787", "#E3B887", "#E3E387", "#87E387", "#87E3E3", "#87ACE3", "#BC87E3",
            // Row 4 – L=42%
            "#808080", "#AC5454", "#AC8954", "#ACAC54", "#54AC54", "#54ACAC", "#547FAC", "#8F54AC",
            // Row 5 – L=28%
            "#484848", "#722E2E", "#72532E", "#72722E", "#2E722E", "#2E7272", "#2E4F72", "#592E72",
            // Row 6 – L=16%
            "#000000", "#411A1A", "#412F1A", "#41411A", "#1A411A", "#1A4141", "#1A2D41", "#331A41",
            // Row 7 – Pure primaries (S=100%, L=50%)
            "#FF0000", "#FFFF00", "#00FF00", "#00FFFF", "#0000FF", "#FF00FF", "#FF8000", "#8000FF",
        ];

    [ObservableProperty]
    private string? _selectedColor;

    /// <summary>All preset colours: 6 harmonic rows (S=62%, even lightness) + 1 pure primaries row.
    /// Columns: Grey, Red, Orange, Yellow, Green, Cyan, Blue, Purple.</summary>
    public string?[] PresetColors => s_presetColors;
}
