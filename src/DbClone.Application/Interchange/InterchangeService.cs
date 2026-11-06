using DbClone.Application.DTOs;
using DbClone.Application.Targets;

using Microsoft.Extensions.Logging;

namespace DbClone.Application.Interchange;

/// <summary>
/// Default implementation of <see cref="IInterchangeService"/>.
/// Builds a <see cref="DbAdapter.Definitions.DatabaseTarget"/> through <see cref="DatabaseTargetFactory"/>
/// and delegates to the interchange product for export, import, inspection, verification, and packing.
/// </summary>
/// <remarks>
/// The product (DatabaseInterchange) is tracked in <c>refactors/04/implementationChecklist.md</c>.
/// Until it arrives, product calls throw <see cref="NotSupportedException"/> so the rest of the
/// application (UI, settings, tests) can be built and exercised against the contract.
/// </remarks>
public sealed class InterchangeService : IInterchangeService
{
    private readonly DatabaseTargetFactory _targetFactory;
    private readonly ILogger<InterchangeService> _logger;

    public InterchangeService(
        DatabaseTargetFactory targetFactory,
        ILogger<InterchangeService> logger)
    {
        _targetFactory = targetFactory ?? throw new ArgumentNullException(nameof(targetFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<InterchangeStoreResult> StoreAsync(
        InterchangeStoreRequest request,
        IProgress<InterchangeProgressView>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = _targetFactory.Create(request.Connection);
        _logger.LogInformation(
            "Store started: catalog={CatalogPath} tables={TableCount} includeData={IncludeData}",
            request.CatalogPath,
            request.TableSelection.ExcludedTables.Count,
            request.IncludeData);

        // TODO: call ISnapshotExporter from the interchange product once it exists.
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        throw new NotSupportedException(
            "The interchange product (DatabaseInterchange) is not yet available. " +
            "See refactors/04/implementationChecklist.md.");
    }

    /// <inheritdoc />
    public async Task<InterchangePlanView> PlanRestoreAsync(
        string catalogPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);

        _logger.LogInformation("Plan restore started: catalog={CatalogPath}", catalogPath);

        // TODO: call IImportPlanner from the interchange product once it exists.
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        throw new NotSupportedException(
            "The interchange product (DatabaseInterchange) is not yet available. " +
            "See refactors/04/implementationChecklist.md.");
    }

    /// <inheritdoc />
    public async Task<InterchangeRestoreResult> RestoreAsync(
        InterchangeRestoreRequest request,
        IProgress<InterchangeProgressView>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = _targetFactory.Create(request.Connection);
        _logger.LogInformation(
            "Restore started: catalog={CatalogPath} target={Dialect}",
            request.CatalogPath,
            target.Dialect);

        // TODO: call ISnapshotImporter from the interchange product once it exists.
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        throw new NotSupportedException(
            "The interchange product (DatabaseInterchange) is not yet available. " +
            "See refactors/04/implementationChecklist.md.");
    }

    /// <inheritdoc />
    public async Task<CatalogSummary> InspectAsync(
        string catalogPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);

        _logger.LogInformation("Inspect started: catalog={CatalogPath}", catalogPath);

        // TODO: call CatalogReader from the interchange product once it exists.
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        throw new NotSupportedException(
            "The interchange product (DatabaseInterchange) is not yet available. " +
            "See refactors/04/implementationChecklist.md.");
    }

    /// <inheritdoc />
    public async Task<VerificationSummary> VerifyAsync(
        string catalogPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);

        _logger.LogInformation("Verify started: catalog={CatalogPath}", catalogPath);

        // TODO: call CatalogVerifier from the interchange product once it exists.
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        throw new NotSupportedException(
            "The interchange product (DatabaseInterchange) is not yet available. " +
            "See refactors/04/implementationChecklist.md.");
    }

    /// <inheritdoc />
    public async Task<string?> PackAsync(
        string catalogPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);

        _logger.LogInformation("Pack started: catalog={CatalogPath}", catalogPath);

        // TODO: call CatalogArchiver.PackAsync from the interchange product once it exists.
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        throw new NotSupportedException(
            "The interchange product (DatabaseInterchange) is not yet available. " +
            "See refactors/04/implementationChecklist.md.");
    }
}
