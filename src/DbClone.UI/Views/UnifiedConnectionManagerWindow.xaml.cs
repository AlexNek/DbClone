using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using Microsoft.Win32;

namespace DbClone.UI.Views;

/// <summary>
/// Code-behind for UnifiedConnectionManagerWindow — handles Escape key and toolbar menus.
/// </summary>
public partial class UnifiedConnectionManagerWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool _sharedMenuCloseHooked;

    private static T? FindAncestor<T>(DependencyObject current)
        where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    public UnifiedConnectionManagerWindow(UnifiedConnectionManagerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Tab content is materialized lazily, so a pre-selection made in the
        // ViewModel constructor can be dropped before the list bindings go
        // live. Re-assert it once the window (and the active tab) has loaded.
        Loaded += (_, _) => viewModel.ApplyInitialSelection();
    }

    private void ConnectionItemMoreDots_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        // Find the parent ListBoxItem to select it
        var listBoxItem = FindAncestor<ListBoxItem>(element);
        if (listBoxItem?.DataContext is SavedConnection connection)
        {
            var vm = (UnifiedConnectionManagerViewModel)DataContext;
            vm.SelectedConnection = connection;
        }

        // Open the single shared context menu (defined once in XAML) at the dots
        OpenSharedMenuAt(element);
        e.Handled = true;
    }

    private void ConnectionItemMoreDots_ButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        var listBoxItem = FindAncestor<ListBoxItem>(element);
        if (listBoxItem?.DataContext is SavedConnection connection)
        {
            var vm = (UnifiedConnectionManagerViewModel)DataContext;
            vm.SelectedConnection = connection;
        }

        OpenSharedMenuAt(element);
        e.Handled = true;
    }

    private void ExportAllToFile_Click(object sender, RoutedEventArgs e)
    {
        var vm = (UnifiedConnectionManagerViewModel)DataContext;
        var dialog = new SaveFileDialog
                         {
                             Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                             DefaultExt = ".json",
                             FileName = $"DbClone-backup-{DateTime.Now:yyyyMMdd}.json",
                             Title = "Export All Connections and Groups"
                         };

        if (dialog.ShowDialog() != true) return;

        // Ask for optional encryption password
        var pwRequest = new BackupPasswordRequest
                            {
                                Title = "Protect Backup File",
                                Description =
                                    "Optionally enter a password to encrypt the backup file. " +
                                    "Leave empty to save without encryption.\n\n" +
                                    "Encrypted backups are safe to store or share — passwords cannot be recovered.",
                                IsConfirmVisible = false
                            };

        var pwDialog = new BackupPasswordDialog { DataContext = pwRequest, Owner = this };

        if (pwDialog.ShowDialog() != true) return;

        try
        {
            vm.ExportAllToPath(dialog.FileName, pwDialog.Password);
            var encrypted = !string.IsNullOrEmpty(pwDialog.Password);
            MessageBox.Show(
                encrypted
                    ? "All connections and groups have been exported and encrypted successfully."
                    : "All connections and groups have been exported successfully.",
                "Export Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to export: {ex.Message}",
                "Export Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ExportToFile_Click(object sender, RoutedEventArgs e)
    {
        var vm = (UnifiedConnectionManagerViewModel)DataContext;
        vm.ExportSelectedToFile();
    }

    private void ImportAllFromFile_Click(object sender, RoutedEventArgs e)
    {
        var vm = (UnifiedConnectionManagerViewModel)DataContext;

        var confirm = MessageBox.Show(
            "This will import all connections and groups from the backup file, " +
            "merging with or replacing any existing entries that have the same ID.\n\n" +
            "Continue?",
            "Import All Connections and Groups",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        var dialog = new OpenFileDialog
                         {
                             Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                             DefaultExt = ".json",
                             Title = "Import All Connections and Groups"
                         };

        if (dialog.ShowDialog() != true) return;

        // Check if file is encrypted by reading the magic header
        string? password = null;
        if (vm.IsBackupEncrypted(dialog.FileName))
        {
            var pwRequest = new BackupPasswordRequest
                                {
                                    Title = "Enter Backup Password",
                                    Description =
                                        "This backup file is encrypted. Enter the password to decrypt it.",
                                    IsConfirmVisible = false
                                };

            var pwDialog = new BackupPasswordDialog { DataContext = pwRequest, Owner = this };

            if (pwDialog.ShowDialog() != true) return;
            password = pwDialog.Password;
        }

        try
        {
            var (connections, groups) = vm.ImportAllFromPath(dialog.FileName, password);
            MessageBox.Show(
                $"Imported {connections} connection(s) and {groups} group(s) successfully.",
                "Import Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show(
                "Incorrect password or the file is corrupted.",
                "Decryption Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to import: {ex.Message}",
                "Import Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ImportFromFile_Click(object sender, RoutedEventArgs e)
    {
        var vm = (UnifiedConnectionManagerViewModel)DataContext;

        var dialog = new OpenFileDialog
                         {
                             Filter =
                                 "Text files (*.txt;*.env;*.cfg)|*.txt;*.env;*.cfg|All files (*.*)|*.*",
                             Title = "Import Connection from File"
                         };

        if (dialog.ShowDialog() != true) return;

        var text = File.ReadAllText(dialog.FileName);
        vm.ImportFromText(text);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        OpenSharedMenuAt(element);
    }

    private void OpenSharedMenuAt(FrameworkElement target)
    {
        var menu = ConnectionsList.ContextMenu;
        if (menu is null) return;

        if (!_sharedMenuCloseHooked)
        {
            _sharedMenuCloseHooked = true;
            menu.Closed += (_, _) =>
                {
                    // Restore the right-click defaults so the shared menu keeps
                    // opening at the cursor position over the list.
                    menu.PlacementTarget = ConnectionsList;
                    menu.Placement = PlacementMode.MousePoint;
                };
        }

        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;

        // A ContextMenu is not part of the visual tree, so it inherits its
        // DataContext from its PlacementTarget. For the per-item "⋮" dots the
        // PlacementTarget is the item template element (DataContext = the
        // SavedConnection), which would leave every menu command unbound and
        // silently dead. Pin the menu to the window's ViewModel so the commands
        // resolve regardless of which element anchored the menu.
        menu.DataContext = DataContext;

        // Defer opening until the triggering click has been fully processed.
        // Opening synchronously inside a mouse-up handler makes WPF deliver the
        // pending button release to the newly captured menu as an "outside
        // click" (PreviewMouseUpOutsideCapturedElement), which dismisses the
        // menu immediately.
        Dispatcher.BeginInvoke(() => menu.IsOpen = true, DispatcherPriority.Background);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}
