using DbClone.Application.DTOs;

namespace DbClone.Application.Interfaces;

public interface IConnectionStringService
{
    string BuildKeyValue(ConnectionStringFields fields);

    /// <summary>
    /// Parses a connection string in any supported format (key-value, URI, etc.).
    /// The provider determines which URI schemes and formats it recognises.
    /// </summary>
    bool TryParse(string value, out ConnectionStringFields fields);

    bool TryParseKeyValue(string value, out ConnectionStringFields fields);
}
