using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbClone.Application.Interfaces;

/// <summary>
/// The application's route to the engine's connection-text syntax: whether a piece of
/// text is a connection string and how to write one. Implemented over the adapter's
/// syntax role so no UI file names a definitions type (D9, D14).
/// </summary>
public interface IConnectionTextService
{
    /// <summary>
    /// The dialect of the active engine, reported by its adapter. Lets the UI stamp a connection's
    /// provider identity without naming an engine (refactors/09 P5). Throws when no engine is installed.
    /// </summary>
    string Dialect { get; }

    /// <summary>The TLS identifiers the active engine offers the connection form.</summary>
    IReadOnlyList<string> SecurityOptions { get; }

    /// <summary>The format id to use when the user picks none, taken from the active engine.</summary>
    string DefaultFormatId { get; }

    /// <summary>Maps a TLS identifier onto the shared enum for boundary types that still take it.</summary>
    ESslMode ToSslMode(string identifier);

    string BuildKeyValue(ConnectionStringFields fields);

    /// <summary>
    /// Parses a connection string in any supported format (key-value, URI, etc.).
    /// The provider determines which URI schemes and formats it recognises.
    /// </summary>
    bool TryParse(string value, out ConnectionStringFields fields);

    bool TryParseKeyValue(string value, out ConnectionStringFields fields);
}
