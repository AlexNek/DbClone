namespace DbAdapter.Definitions;

/// <summary>
/// The identity of an engine adapter: which dialect it speaks, which engine versions it
/// covers and which version of the adapter itself is loaded.
/// </summary>
/// <param name="Dialect">Stable engine identifier, e.g. "postgresql".</param>
/// <param name="EngineVersionRange">Version range the adapter declares support for, e.g. "*".</param>
/// <param name="AdapterVersion">Version of the adapter assembly.</param>
public sealed record AdapterInfo(
    string Dialect,
    string EngineVersionRange,
    string AdapterVersion);
