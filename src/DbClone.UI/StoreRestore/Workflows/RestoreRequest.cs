using DbClone.Application.DTOs;

namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Request for the restore command.
/// </summary>
public sealed record RestoreRequest(
    ConnectionInfo Connection,
    string CatalogPath,
    string UnsupportedFeaturePolicy);
