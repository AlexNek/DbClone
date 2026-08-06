using DbClone.Application.Enums;

namespace DbClone.Application.DTOs;

/// <summary>
/// A warning produced during connection string import (e.g. missing password, unknown parameter).
/// </summary>
public sealed record ImportWarning(
    EWarningLevel Level,
    string Message,
    string? ParameterName = null);
