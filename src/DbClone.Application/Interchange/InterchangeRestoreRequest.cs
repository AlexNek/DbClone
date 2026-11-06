using DbClone.Application.DTOs;

namespace DbClone.Application.Interchange;

/// <summary>
/// Application-level request to restore (import) a catalog into a database.
/// v1 restores into an empty target only; no safety-mode enum.
/// </summary>
public sealed record InterchangeRestoreRequest(
    string CatalogPath,
    ConnectionInfo Connection,
    string UnsupportedFeaturePolicy);
