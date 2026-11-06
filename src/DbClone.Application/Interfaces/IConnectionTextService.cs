using CanonicalModel.DTOs;

namespace DbClone.Application.Interfaces;

/// <summary>
/// The application's route to the engine's connection-text syntax: whether a piece of
/// text is a connection string and how to write one. Implemented over the adapter's
/// syntax role so no UI file names a definitions type (D9, D14).
/// </summary>
public interface IConnectionTextService
{
    string BuildKeyValue(ConnectionStringFields fields);

    /// <summary>
    /// Parses a connection string in any supported format (key-value, URI, etc.).
    /// The provider determines which URI schemes and formats it recognises.
    /// </summary>
    bool TryParse(string value, out ConnectionStringFields fields);

    bool TryParseKeyValue(string value, out ConnectionStringFields fields);
}
