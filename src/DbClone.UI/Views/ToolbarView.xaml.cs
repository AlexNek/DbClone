using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using Wpf.Ui.Appearance;

namespace DbClone.UI.Views;

public partial class ToolbarView : UserControl
{
    public ToolbarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplicationThemeManager.Changed += OnThemeChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ApplicationThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        // Re-apply mode visuals after theme switch so brushes resolve to new theme resources
        if (DataContext is ToolbarViewModel vm)
            Dispatcher.InvokeAsync(() => ApplyModeVisuals(vm.SelectedMode));
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;

        if (e.NewValue is INotifyPropertyChanged newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            if (DataContext is ToolbarViewModel vm)
                ApplyModeVisuals(vm.SelectedMode);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolbarViewModel.SelectedMode) && DataContext is ToolbarViewModel vm)
            ApplyModeVisuals(vm.SelectedMode);
    }

    private void CopyTab_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ToolbarViewModel vm)
            vm.SelectedMode = EWorkspaceMode.Copy;
    }

    private void CompareTab_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ToolbarViewModel vm)
            vm.SelectedMode = EWorkspaceMode.Compare;
    }

    private void ApplyModeVisuals(EWorkspaceMode mode)
    {
        // Resolve brushes fresh from current theme resources
        var accentBrush = (Brush)FindResource("SystemAccentColorPrimaryBrush");
        var dimText = (Brush)FindResource("TextFillColorSecondaryBrush");
        var trackBg = (Brush)FindResource("ControlFillColorDefaultBrush");
        var trackBorder = (Brush)FindResource("ControlStrokeColorDefaultBrush");

        // Update track for current theme
        ModeSelectorTrack.Background = trackBg;
        ModeSelectorTrack.BorderBrush = trackBorder;

        if (mode == EWorkspaceMode.Copy)
        {
            CopyTab.Background = accentBrush;
            CopyTabText.Foreground = Brushes.White;
            CopyTabText.FontWeight = FontWeights.SemiBold;

            CompareTab.Background = Brushes.Transparent;
            CompareTabText.Foreground = dimText;
            CompareTabText.FontWeight = FontWeights.Normal;
        }
        else
        {
            CompareTab.Background = accentBrush;
            CompareTabText.Foreground = Brushes.White;
            CompareTabText.FontWeight = FontWeights.SemiBold;

            CopyTab.Background = Brushes.Transparent;
            CopyTabText.Foreground = dimText;
            CopyTabText.FontWeight = FontWeights.Normal;
        }
    }

    private void CopyOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        CopyOptionsPopup.DataContext = DataContext;
        CopyOptionsPopup.IsOpen = !CopyOptionsPopup.IsOpen;
        Debug.WriteLine(
            $"[CopyOptions] Popup DataContext = {CopyOptionsPopup.DataContext?.GetType().Name ?? "null"}");
    }

    private void CompareOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        CompareOptionsPopup.DataContext = DataContext;
        CompareOptionsPopup.IsOpen = !CompareOptionsPopup.IsOpen;
        Debug.WriteLine(
            $"[CompareOptions] Popup DataContext = {CompareOptionsPopup.DataContext?.GetType().Name ?? "null"}");
    }
}
