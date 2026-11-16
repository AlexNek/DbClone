using CanonicalModel.Enums;

namespace CanonicalModel.Models;

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
    bool IsProcedure)
{
    /// <summary>
    /// Names of the server settings the definition declares in its <c>SET</c> clauses,
    /// one per clause and never their values. Empty when the function declares none.
    /// </summary>
    public IReadOnlyList<string> SetParameters { get; init; } = [];
}
