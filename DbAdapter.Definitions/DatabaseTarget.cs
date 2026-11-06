namespace DbAdapter.Definitions;

/// <summary>
/// The only connection shape that crosses the boundary: which dialect and one complete
/// connection string carrying host, port, database, user, password and TLS.
/// The application builds it in <c>DatabaseTargetFactory</c>; inside a block the text is
/// the only source.
/// </summary>
/// <param name="Dialect">Stable engine identifier, e.g. "postgresql".</param>
/// <param name="ConnectionString">Complete connection string in the engine's own syntax.</param>
public sealed record DatabaseTarget(
    string Dialect,
    string ConnectionString);
