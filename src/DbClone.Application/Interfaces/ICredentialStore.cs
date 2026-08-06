namespace DbClone.Application.Interfaces;

/// <summary>
/// Manages credential storage and retrieval.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Removes stored credentials.
    /// </summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves stored credentials.
    /// </summary>
    Task<(string Username, string Password)?> RetrieveAsync(
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores credentials securely.
    /// </summary>
    Task StoreAsync(
        string key,
        string username,
        string password,
        CancellationToken cancellationToken = default);
}
