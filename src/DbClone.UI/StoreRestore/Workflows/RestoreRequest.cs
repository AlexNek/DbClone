using DbClone.Application.DTOs;
using DbClone.Application.Interchange;

namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Request for the restore command.
/// </summary>
/// <remarks>
/// <c>Progress</c> is the caller's sink for the run's stage and object reports and defaults to null, so
/// a caller that shows no progress says nothing and gets the silence it had before.
/// </remarks>
public sealed record RestoreRequest(
    ConnectionInfo Connection,
    string CatalogPath,
    string UnsupportedFeaturePolicy,
    IProgress<InterchangeProgressView>? Progress = null);
