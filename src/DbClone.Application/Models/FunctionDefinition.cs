using DbClone.Application.Enums;

namespace DbClone.Application.Models;

/// <summary>
/// Represents a function or procedure definition.
/// </summary>
public sealed record FunctionDefinition(
    string SchemaName,
    string Name,
    string Language,
    string ReturnType,
    string Definition,
    IReadOnlyList<FunctionParameter> Parameters,
    EFunctionVolatility Volatility,
    bool IsStrict,
    bool SecurityDefiner,
    string? Comment,
    bool IsProcedure);
