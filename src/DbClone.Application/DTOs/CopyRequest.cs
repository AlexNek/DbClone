namespace DbClone.Application.DTOs;

/// <summary>
/// Represents a request to copy a database.
/// </summary>
public sealed record CopyRequest(
    ConnectionInfo Source,
    ConnectionInfo Destination,
    CopyOptions Options);
