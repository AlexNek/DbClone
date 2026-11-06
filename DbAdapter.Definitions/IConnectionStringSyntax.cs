using CanonicalModel.DTOs;

namespace DbAdapter.Definitions;

/// <summary>
/// The connection-text role: whether a piece of text is a connection string of this
/// engine, and how to write one. Stateless — one instance per engine.
/// </summary>
public interface IConnectionStringSyntax
{
    /// <summary>The engine's own text formats, used for import detection and export.</summary>
    IReadOnlyList<IConnectionTextFormat> Formats { get; }

    string BuildKeyValue(ConnectionStringFields fields);

    /// <summary>
    /// Parses a connection string in any supported format (key-value, URI, etc.).
    /// The provider determines which URI schemes and formats it recognises.
    /// </summary>
    bool TryParse(string value, out ConnectionStringFields fields);

    bool TryParseKeyValue(string value, out ConnectionStringFields fields);
}
