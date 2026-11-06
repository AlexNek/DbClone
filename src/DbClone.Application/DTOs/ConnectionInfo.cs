namespace DbClone.Application.DTOs;

/// <summary>
/// Connection information for a database server.
/// </summary>
/// <param name="Host">Server host name or IP address.</param>
/// <param name="Port">Server port.</param>
/// <param name="DatabaseName">Target database name.</param>
/// <param name="Username">Login user.</param>
/// <param name="Password">Login password.</param>
/// <param name="SslMode">The engine's TLS identifier (e.g. "Prefer"), not the shared enum: the
/// product carries the adapter's own name and converts at the boundary (refactors/09 P7).</param>
/// <param name="Engine">The dialect that selects the adapter (refactors/09 P4). Null on files
/// written before the engine was recorded, which the single installed adapter serves.</param>
public sealed record ConnectionInfo(
    string Host,
    int Port,
    string DatabaseName,
    string Username,
    string Password,
    string SslMode,
    string? Engine = null);
