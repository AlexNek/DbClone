using DbClone.Application.Enums;

namespace DbClone.Application.Models;

/// <summary>
/// Represents a function parameter.
/// </summary>
public sealed record FunctionParameter(
    string Name,
    string DataType,
    EParameterMode Mode,
    string? DefaultValue);
