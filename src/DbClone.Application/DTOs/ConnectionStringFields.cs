namespace DbClone.Application.DTOs;

public sealed record ConnectionStringFields(
    string Host,
    int Port,
    string Database,
    string Username,
    string Password,
    string SslMode);
