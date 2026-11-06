using System.Windows.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.Interchange;
using DbClone.UI.Models;
using DbClone.UI.Services;
using DbClone.UI.Settings;

using Microsoft.Extensions.Logging;

using Wpf.Ui.Controls;

namespace DbClone.UI.ViewModels.Workspaces.StoreRestore;

/// <summary>
/// Workspace ViewModel for the Store / Restore feature.
/// Implements <see cref="IWorkflowViewModel"/> so it joins the existing toolbar,
/// log pane, and shortcut infrastructure alongside Copy and Compare.
/// </summary>
/// <remarks>
/// The interchange product (DatabaseInterchange) is not yet available.
/// All product calls go through <see cref="IInterchangeService"/>, which throws
/// <see cref="NotSupportedException"/> until the product arrives. The workspace
/// shell is fully functional — settings, logging, cancellation —
/// so the UI can be exercised end-to-end before the product integrates.
/// </remarks>
public sealed partial class StoreRestoreViewModel : ObservableObject, IWorkflowViewModel
{
    private readonly IInterchangeService _interchangeService;

    private readonly ILogger<StoreRestoreViewModel> _logger;

    private readonly SettingsPersistenceManager _settingsPersister;

    private readonly UserSettings _settings;

    private CancellationTokenSource? _operationCts;

    /// <summary>Direction switch: Store (database → catalog) or Restore (catalog → database).</summary>
    [ObservableProperty]
    private bool _isStoreDirection = true;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isPaused;

    /// <summary>Per-workflow UI state (logs, banner, status, objects panel).</summary>
    public WorkflowState State { get; } = new();

    /// <inheritdoc />
    bool IWorkflowViewModel.IsRunning => IsRunning;

    /// <inheritdoc />
    ICommand IWorkflowViewModel.PauseCommand => PauseCommand;

    /// <inheritdoc />
    ICommand IWorkflowViewModel.StopCommand => StopCommand;

    public StoreRestoreViewModel(
        IInterchangeService interchangeService,
        SettingsPersistenceManager settingsPersister,
        UserSettings settings,
        ILogger<StoreRestoreViewModel> logger)
    {
        _interchangeService = interchangeService ?? throw new ArgumentNullException(nameof(interchangeService));
        _settingsPersister = settingsPersister ?? throw new ArgumentNullException(nameof(settingsPersister));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Switches the workspace to Store direction (database → catalog).</summary>
    [RelayCommand]
    private void SwitchToStore()
    {
        IsStoreDirection = true;
        _logger.LogInformation("Switched to Store direction");
    }

    /// <summary>Switches the workspace to Restore direction (catalog → database).</summary>
    [RelayCommand]
    private void SwitchToRestore()
    {
        IsStoreDirection = false;
        _logger.LogInformation("Switched to Restore direction");
    }

    /// <summary>
    /// Starts a store operation (database → catalog).
    /// Currently blocked: the interchange product is not yet available.
    /// </summary>
    [RelayCommand]
    private async Task StartStoreAsync(CancellationToken cancellationToken)
    {
        IsStoreDirection = true;
        _settingsPersister.SaveNow();
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _operationCts.Token;

        _logger.LogInformation("Store operation requested");
        IsRunning = true;
        State.BeginNewRun();
        State.Log("Store: preparing catalog export...");

        try
        {
            var options = _settings.StoreRestore;
            State.Log($"  Catalog directory: {options.DefaultCatalogDirectory}");
            State.Log($"  Chunk size: {options.ChunkBytes:N0} bytes, rows/chunk: {options.RowsPerChunk:N0}");
            State.Log($"  Include data: {options.IncludeData}, pack after store: {options.PackAfterStore}");

            // Simulate operation delay so UI can show progress/banner
            State.Log("Simulating store operation...");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);

            // The product call would go here. Until then, the service throws NotSupportedException.
            // var request = new InterchangeStoreRequest(...);
            // var result = await _interchangeService.StoreAsync(request, progress, ct);

            ct.ThrowIfCancellationRequested();

            throw new NotSupportedException(
                "The interchange product (DatabaseInterchange) is not yet available. " +
                "See refactors/04/implementationChecklist.md.");
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex, "Store operation blocked: product not available");
            State.LogWarning("Store is not yet available — the interchange product has not been integrated.");
            State.ShowBanner(
                "Store not yet available",
                "The DatabaseInterchange product is tracked in refactors/04. " +
                "The workspace shell is ready; store will work once the product integrates.",
                InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            State.Log("Store cancelled by user.");
            State.StatusBarSummary = "Store cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Store operation failed");
            State.LastError = ex.Message;
            State.LogError($"Store failed: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    /// <summary>
    /// Starts a restore operation (catalog → database).
    /// Currently blocked: the interchange product is not yet available.
    /// </summary>
    [RelayCommand]
    private async Task StartRestoreAsync(CancellationToken cancellationToken)
    {
        IsStoreDirection = false;
        _settingsPersister.SaveNow();
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _operationCts.Token;

        _logger.LogInformation("Restore operation requested");
        IsRunning = true;
        State.BeginNewRun();
        State.Log("Restore: preparing catalog import...");

        try
        {
            var options = _settings.StoreRestore;
            State.Log($"  Catalog directory: {options.DefaultCatalogDirectory}");
            State.Log($"  Unsupported feature policy: {options.UnsupportedFeaturePolicy}");

            // Simulate operation delay so UI can show progress/banner
            State.Log("Simulating restore operation...");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);

            // The product call would go here. Until then, the service throws NotSupportedException.
            // var request = new InterchangeRestoreRequest(...);
            // var result = await _interchangeService.RestoreAsync(request, progress, ct);

            ct.ThrowIfCancellationRequested();

            throw new NotSupportedException(
                "The interchange product (DatabaseInterchange) is not yet available. " +
                "See refactors/04/implementationChecklist.md.");
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex, "Restore operation blocked: product not available");
            State.LogWarning("Restore is not yet available — the interchange product has not been integrated.");
            State.ShowBanner(
                "Restore not yet available",
                "The DatabaseInterchange product is tracked in refactors/04. " +
                "The workspace shell is ready; restore will work once the product integrates.",
                InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            State.Log("Restore cancelled by user.");
            State.StatusBarSummary = "Restore cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore operation failed");
            State.LastError = ex.Message;
            State.LogError($"Restore failed: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    /// <summary>Pauses or resumes the running operation.</summary>
    [RelayCommand]
    private void Pause()
    {
        if (!IsRunning) return;
        IsPaused = !IsPaused;
        State.Log(IsPaused ? "Operation paused" : "Operation resumed");
    }

    /// <summary>Stops the running operation via cancellation token.</summary>
    [RelayCommand]
    private void Stop()
    {
        if (_operationCts is { IsCancellationRequested: false })
        {
            State.Log("Stop requested — cancelling operation...");
            _operationCts.Cancel();
        }
    }
}
