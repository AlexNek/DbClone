using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbAdapter.Definitions;

/// <summary>
/// The connection-text role: whether a piece of text is a connection string of this
/// engine, and how to write one. Stateless — one instance per engine.
/// </summary>
public interface IConnectionStringSyntax
{
    /// <summary>The engine's own text formats, used for import detection and export.</summary>
    IReadOnlyList<IConnectionTextFormat> Formats { get; }

    /// <summary>
    /// The TLS identifiers this engine offers the connection form, in display order. The product
    /// shows these verbatim rather than naming any engine's TLS vocabulary (refactors/09 P7).
    /// </summary>
    IReadOnlyList<string> SecurityOptions { get; }

    /// <summary>
    /// The format id to use when the user picks none — the engine's canonical text format.
    /// </summary>
    string DefaultFormatId { get; }

    string BuildKeyValue(ConnectionStringFields fields);

    /// <summary>
    /// Maps a TLS identifier (as offered by <see cref="SecurityOptions"/>) onto the shared
    /// <see cref="ESslMode"/> for the boundary types that still carry the enum. The engine maps
    /// its own names and answers for an unknown identifier.
    /// </summary>
    ESslMode ToSslMode(string identifier);

    /// <summary>
    /// Parses a connection string in any supported format (key-value, URI, etc.).
    /// The provider determines which URI schemes and formats it recognises.
    /// </summary>
    bool TryParse(string value, out ConnectionStringFields fields);

    bool TryParseKeyValue(string value, out ConnectionStringFields fields);
}
