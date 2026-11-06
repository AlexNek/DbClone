namespace DbAdapter.Definitions;

/// <summary>
/// The identity of an engine adapter: which dialect it speaks, which engine versions it
/// covers and which version of the adapter itself is loaded.
/// </summary>
/// <param name="Id">Internal adapter identifier, never shown to users. Stable, machine-readable, e.g. "org.postgresql.adapter". Used for logging, diagnostics and distinguishing multiple adapters for the same dialect.</param>
/// <param name="Dialect">Stable engine identifier, e.g. "postgresql". User-visible and appears in connection strings.</param>
/// <param name="EngineVersionRange">Version range the adapter declares support for, e.g. "*".</param>
/// <param name="AdapterVersion">Version of the adapter assembly.</param>
public sealed record AdapterInfo(
    string Id,
    string Dialect,
    string EngineVersionRange,
    string AdapterVersion);
