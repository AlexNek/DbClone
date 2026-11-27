using CanonicalModel.Interchange;

namespace DbAdapter.Definitions;

/// <summary>
/// Adapter-level capabilities (independent of any specific target).
/// These describe what the adapter can do in general, not what a specific server supports.
/// </summary>
public interface IDatabaseCapabilities
{
    /// <summary>
    /// Whether this adapter can create the specified object kind during schema planning.
    /// Additional capability members should be added when a consumer decision exists.
    /// </summary>
    bool CanCreate(EObjectKind kind);
}
