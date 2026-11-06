using CanonicalModel.DTOs;
using CanonicalModel.Enums;
using DbAdapter.Definitions;
using DbClone.Application.Adapters;
using DbClone.Application.Interfaces;

namespace DbClone.Application.Services;

/// <summary>
/// Forwards the connection-text questions to the syntax role of the installed adapter, so the UI
/// parses and writes connection strings without naming any engine or definitions type (D9, D14).
/// The adapter is resolved at call time, never at construction, so an empty catalog does not stop
/// the application from starting (refactors/09 P8).
/// </summary>
public sealed class ConnectionTextService : IConnectionTextService
{
    private readonly IAdapterCatalog _catalog;

    public ConnectionTextService(IAdapterCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    private IConnectionStringSyntax Syntax => _catalog.Resolve(null).Syntax;

    /// <inheritdoc />
    public string Dialect => _catalog.Resolve(null).Info.Dialect;

    /// <inheritdoc />
    public IReadOnlyList<string> SecurityOptions => Syntax.SecurityOptions;

    /// <inheritdoc />
    public string DefaultFormatId => Syntax.DefaultFormatId;

    /// <inheritdoc />
    public ESslMode ToSslMode(string identifier) => Syntax.ToSslMode(identifier);

    /// <inheritdoc />
    public string BuildKeyValue(ConnectionStringFields fields) => Syntax.BuildKeyValue(fields);

    /// <inheritdoc />
    public bool TryParse(string value, out ConnectionStringFields fields) => Syntax.TryParse(value, out fields);

    /// <inheritdoc />
    public bool TryParseKeyValue(string value, out ConnectionStringFields fields) =>
        Syntax.TryParseKeyValue(value, out fields);
}
