using CanonicalModel.Enums;

namespace CanonicalModel.DTOs;

public sealed record ConnectionStringFields(
    string Host,
    int Port,
    string Database,
    string Username,
    string Password,
    ESslMode SslMode,
    string Provider = "",
    Dictionary<string, string>? Options = null);
