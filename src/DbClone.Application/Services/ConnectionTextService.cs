using CanonicalModel.DTOs;
using DbAdapter.Definitions;
using DbClone.Application.Interfaces;

namespace DbClone.Application.Services;

/// <summary>
/// Forwards the connection-text questions to the adapter's syntax role, so the UI parses
/// and writes connection strings without naming any engine or definitions type (D9, D14).
/// </summary>
public sealed class ConnectionTextService : IConnectionTextService
{
    private readonly IConnectionStringSyntax _syntax;

    public ConnectionTextService(IDatabaseAdapter adapter) =>
        _syntax = (adapter ?? throw new ArgumentNullException(nameof(adapter))).Syntax;

    /// <inheritdoc />
    public string BuildKeyValue(ConnectionStringFields fields) => _syntax.BuildKeyValue(fields);

    /// <inheritdoc />
    public bool TryParse(string value, out ConnectionStringFields fields) => _syntax.TryParse(value, out fields);

    /// <inheritdoc />
    public bool TryParseKeyValue(string value, out ConnectionStringFields fields) =>
        _syntax.TryParseKeyValue(value, out fields);
}
