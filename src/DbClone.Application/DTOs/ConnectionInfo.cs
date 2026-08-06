using DbClone.Application.Enums;

namespace DbClone.Application.DTOs;

/// <summary>
/// Connection information for a database server.
/// </summary>
public sealed record ConnectionInfo(
    string Host,
    int Port,
    string DatabaseName,
    string Username,
    string Password,
    ESslMode SslMode);
