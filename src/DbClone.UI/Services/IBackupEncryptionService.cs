namespace DbClone.UI.Services;

/// <summary>
/// Service for encrypting and decrypting backup files.
/// </summary>
public interface IBackupEncryptionService
{
    /// <summary>
    /// Checks whether a file is encrypted by reading its magic header.
    /// </summary>
    bool IsEncrypted(string filePath);

    /// <summary>
    /// Reads a backup file, auto-detecting whether it is encrypted.
    /// </summary>
    /// <param name="filePath">Source file path.</param>
    /// <param name="password">Password for decryption (null if file is not encrypted).</param>
    /// <returns>The decrypted JSON content.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Thrown when the file is encrypted but no password is provided, or the password is wrong.
    /// </exception>
    string ReadBackup(string filePath, string? password);

    /// <summary>
    /// Encrypts plain text content and writes it to a file.
    /// </summary>
    /// <param name="filePath">Destination file path.</param>
    /// <param name="plainText">Content to encrypt.</param>
    /// <param name="password">Password for encryption.</param>
    void WriteEncrypted(string filePath, string plainText, string password);
}
