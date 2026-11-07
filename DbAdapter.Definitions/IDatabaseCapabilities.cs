using CanonicalModel.Interchange;

namespace DbAdapter.Definitions;

/// <summary>
/// Adapter-level capabilities (independent of any specific target).
/// These describe what the adapter can do in general, not what a specific server supports.
/// </summary>
public interface IDatabaseCapabilities
{
    /// <summary>
    /// Whether this adapter can read the specified object kind.
    /// </summary>
    bool CanRead(EObjectKind kind);

    /// <summary>
    /// Whether this adapter can preserve the specified object kind as native text.
    /// </summary>
    bool CanPreserve(EObjectKind kind);

    /// <summary>
    /// Whether this adapter can create the specified object kind on a target.
    /// </summary>
    bool CanCreate(EObjectKind kind);

    /// <summary>
    /// Whether this adapter can restore data.
    /// </summary>
    bool CanRestoreData { get; }

    /// <summary>
    /// Whether this adapter can restore security objects.
    /// </summary>
    bool CanRestoreSecurity { get; }

    /// <summary>
    /// Gets the snapshot consistency modes supported by this adapter.
    /// </summary>
    IReadOnlyList<ESnapshotConsistency> SupportedSnapshotModes { get; }

    /// <summary>
    /// Whether this adapter supports transactional import (per-stage, per-batch).
    /// </summary>
    bool SupportsTransactionalImport { get; }

    /// <summary>
    /// Whether this adapter supports importing into a non-empty target.
    /// </summary>
    bool SupportsExistingTarget { get; }

    /// <summary>
    /// Gets the row order modes supported by this adapter.
    /// </summary>
    IReadOnlyList<ERowOrder> SupportedRowOrders { get; }

    /// <summary>
    /// Gets the schema stages supported by this adapter.
    /// </summary>
    IReadOnlyList<SchemaStage> SupportedSchemaStages { get; }

    /// <summary>
    /// Gets the security stages supported by this adapter.
    /// </summary>
    IReadOnlyList<ESecurityStage> SupportedSecurityStages { get; }
}
