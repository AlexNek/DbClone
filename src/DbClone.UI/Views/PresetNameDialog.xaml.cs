using System.Windows;

namespace DbClone.UI.Views;

/// <summary>
/// Asks for a preset name (Save As… / Rename). Empty names are rejected here;
/// full validation (reserved name, duplicates) runs in the view model.
/// </summary>
public partial class PresetNameDialog : Window
{
    /// <summary>Initial text for the name box (rename flow).</summary>
    public string InitialName { get; init; } = string.Empty;

    /// <summary>Optional disclosure text shown above the name box.</summary>
    public string? Disclosure { get; init; }

    /// <summary>The validated (non-empty, trimmed) preset name.</summary>
    public string PresetName { get; private set; } = string.Empty;

    /// <summary>Initializes a new instance.</summary>
    public PresetNameDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!string.IsNullOrEmpty(Disclosure))
            {
                DisclosureText.Text = Disclosure;
            }
            else
            {
                DisclosureText.Visibility = Visibility.Collapsed;
            }

            NameBox.Text = InitialName;
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();

        if (name.Length == 0)
        {
            ErrorText.Text = "Preset name must not be empty.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        PresetName = name;
        DialogResult = true;
        Close();
    }
}
