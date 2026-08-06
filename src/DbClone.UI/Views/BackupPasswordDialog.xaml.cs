using System.Windows;

namespace DbClone.UI.Views;

/// <summary>
/// Simple dialog that asks the user for an optional encryption password
/// (export) or a decryption password (import).
/// </summary>
public partial class BackupPasswordDialog : Window
{
    /// <summary>The password entered by the user (empty = no encryption).</summary>
    public string? Password { get; private set; }

    public BackupPasswordDialog()
    {
        InitializeComponent();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OkClick(object sender, RoutedEventArgs e)
    {
        // The DataContext is set by the caller and already validated.
        // Just grab the password and close.
        if (DataContext is BackupPasswordRequest request)
        {
            Password = request.Password;
            DialogResult = true;
            Close();
        }
    }
}

/// <summary>
/// Plain request object passed as DataContext to <see cref="BackupPasswordDialog"/>.
/// </summary>
public sealed class BackupPasswordRequest
{
    public string? ConfirmPassword { get; set; }

    public string Description { get; init; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public bool IsConfirmVisible { get; init; }

    public string? Password { get; set; }

    public string Title { get; init; } = "Backup Password";
}
