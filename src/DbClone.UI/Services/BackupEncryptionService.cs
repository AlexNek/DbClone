using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DbClone.UI.Services;

/// <summary>
/// AES-256-CBC encryption service for backup files.
/// Uses PBKDF2-SHA256 for key derivation with random salt.
/// </summary>
public sealed class BackupEncryptionService : IBackupEncryptionService
{
    private const int IvSize = 16;

    private const int KeySizeBytes = 32; // 256 bits

    private const int Pbkdf2Iterations = 600_000;

    private const int SaltSize = 16;

    /// <summary>
    /// Magic header identifying an encrypted backup file.
    /// Legacy marker retained as-is for backward compatibility: renaming it would
    /// change the on-disk format and make existing encrypted backups undecryptable.
    /// Do not change this value.
    /// </summary>
    private static readonly byte[] EncryptedMagic = "PGCLONE_ENC_V1"u8.ToArray();

    /// <inheritdoc />
    public bool IsEncrypted(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            var buffer = new byte[EncryptedMagic.Length];
            if (fs.Read(buffer, 0, buffer.Length) < EncryptedMagic.Length)
                return false;
            return buffer.AsSpan().SequenceEqual(EncryptedMagic);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public string ReadBackup(string filePath, string? password)
    {
        var fileBytes = File.ReadAllBytes(filePath);

        if (IsEncryptedBytes(fileBytes))
        {
            if (string.IsNullOrEmpty(password))
                throw new CryptographicException(
                    "This backup file is encrypted. A password is required.");

            int offset = EncryptedMagic.Length;
            var salt = fileBytes[offset..(offset + SaltSize)];
            offset += SaltSize;
            var iv = fileBytes[offset..(offset + IvSize)];
            offset += IvSize;
            var cipherText = fileBytes[offset..];

            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = DeriveKey(password, salt);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            var plainBytes = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }

        // Plain JSON file
        return Encoding.UTF8.GetString(fileBytes);
    }

    /// <inheritdoc />
    public void WriteEncrypted(string filePath, string plainText, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var iv = RandomNumberGenerator.GetBytes(IvSize);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = DeriveKey(password, salt);
        aes.IV = iv;

        using var fs = File.Create(filePath);
        fs.Write(EncryptedMagic);
        fs.Write(salt);
        fs.Write(iv);

        using var encryptor = aes.CreateEncryptor();
        using var crypto = new CryptoStream(fs, encryptor, CryptoStreamMode.Write);
        var bytes = Encoding.UTF8.GetBytes(plainText);
        crypto.Write(bytes, 0, bytes.Length);
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySizeBytes);
    }

    private static bool IsEncryptedBytes(byte[] fileBytes)
    {
        return fileBytes.Length >= EncryptedMagic.Length + SaltSize + IvSize &&
               fileBytes.AsSpan(0, EncryptedMagic.Length).SequenceEqual(EncryptedMagic);
    }
}
