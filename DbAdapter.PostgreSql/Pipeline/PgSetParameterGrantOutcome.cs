namespace DbAdapter.PostgreSql.Pipeline;

/// <summary>
/// What one parameter's request for <c>SET</c> ended in.
/// </summary>
/// <param name="Parameter">The setting's name, as the source's functions spell it.</param>
/// <param name="Granted">True when the statement ran, false when the server refused it.</param>
/// <param name="Statement">The statement the stage ran, quoted and ready to hand to an administrator.</param>
/// <param name="Reason">The server's message when the statement was refused, otherwise null.</param>
public sealed record PgSetParameterGrantOutcome(
    string Parameter,
    bool Granted,
    string Statement,
    string? Reason = null);
