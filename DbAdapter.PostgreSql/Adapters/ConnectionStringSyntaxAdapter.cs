using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Providers;
using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbAdapter.PostgreSql.Adapters;

/// <summary>
/// The connection-text role: the engine's own parsing and writing, plus the text formats
/// it offers for import detection and export.
/// </summary>
internal sealed class ConnectionStringSyntaxAdapter : IConnectionStringSyntax
{
    private readonly PgConnectionStringService _service;

    public ConnectionStringSyntaxAdapter(
        PgConnectionStringService service,
        IEnumerable<IConnectionTextFormat> formats)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Formats = [..formats];
    }

    /// <inheritdoc />
    public IReadOnlyList<IConnectionTextFormat> Formats { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> SecurityOptions { get; } = ["Disable", "Prefer", "Require"];

    /// <inheritdoc />
    public string DefaultFormatId => "pg-npgsql";

    /// <inheritdoc />
    public string BuildKeyValue(ConnectionStringFields fields) => _service.BuildKeyValue(fields);

    /// <inheritdoc />
    public ESslMode ToSslMode(string identifier) => identifier switch
    {
        "Disable" => ESslMode.Disable,
        "Require" => ESslMode.Require,
        _ => ESslMode.Prefer
    };

    /// <inheritdoc />
    public bool TryParse(string value, out ConnectionStringFields fields) =>
        _service.TryParse(value, out fields);

    /// <inheritdoc />
    public bool TryParseKeyValue(string value, out ConnectionStringFields fields) =>
        _service.TryParseKeyValue(value, out fields);
}
