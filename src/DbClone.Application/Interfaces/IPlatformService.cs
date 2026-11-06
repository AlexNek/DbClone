using CanonicalModel.Platforms;

namespace DbClone.Application.Interfaces;

/// <summary>
/// The application's route to the engine's platform definitions: the hosted-variant
/// list, their connection defaults and host detection. Implemented over the adapter's
/// platform role so no UI file names a definitions type (D9, D13).
/// </summary>
public interface IPlatformService
{
    /// <summary>Every hosted variant the engine declares, plain engine included.</summary>
    IReadOnlyList<PlatformEntry> GetAllPlatforms();

    /// <summary>Stable id of the hosted variant a host belongs to, or null for the plain engine.</summary>
    string? DetectPlatformId(string host);

    /// <summary>Connection defaults (port, TLS mode) of a hosted variant.</summary>
    PlatformDefaults GetConnectionDefaults(string? platformId);
}
