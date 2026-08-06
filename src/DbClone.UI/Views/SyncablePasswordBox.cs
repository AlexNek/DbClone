namespace DbClone.UI.Views;

/// <summary>
/// Fixes a WPF-UI 4.3.0 <see cref="Wpf.Ui.Controls.PasswordBox"/> defect:
/// when the password is revealed and the <c>Password</c> dependency property
/// changes externally (e.g. via a TwoWay binding when the user selects a
/// different connection), the base implementation overwrites the new value
/// with the stale displayed text instead of updating the display.
/// This subclass syncs the displayed text to the incoming password value,
/// keeping both the control and the bound source consistent.
/// </summary>
public sealed class SyncablePasswordBox : Wpf.Ui.Controls.PasswordBox
{
    /// <inheritdoc/>
    protected override void OnPasswordChanged()
    {
        if (IsPasswordRevealed)
        {
            var incoming = Password ?? string.Empty;
            if (Text != incoming)
            {
                // Update the displayed text to match the new password.
                // The base OnTextChanged → HandleRevealedModeUpdate path sees
                // Password == Text and performs no write-back, so the new
                // value is preserved in both the control and the binding source.
                SetCurrentValue(TextProperty, incoming);
                RaiseEvent(new System.Windows.RoutedEventArgs(PasswordChangedEvent));
            }

            return;
        }

        base.OnPasswordChanged();
    }
}
