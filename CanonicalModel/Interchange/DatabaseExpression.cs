namespace CanonicalModel.Interchange;

/// <summary>
/// A database expression (e.g., a check constraint expression, a default value expression).
/// The format records the text but never parses it.
/// </summary>
/// <param name="Dialect">The dialect this expression is written in.</param>
/// <param name="Text">The expression text.</param>
/// <param name="Language">Optional language identifier (e.g., "sql", "plpgsql").</param>
public sealed record DatabaseExpression(
    string Dialect,
    string Text,
    string? Language = null)
{
    /// <inheritdoc />
    public override string ToString() => Language is null ? Text : $"[{Language}] {Text}";
}
