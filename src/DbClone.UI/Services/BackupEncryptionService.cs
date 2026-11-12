using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DbClone.UI.Services;

/// <summary>
/// AES-GCM encryption service for backup files.
/// Version 2 uses PBKDF2-SHA256 for key derivation and authenticates the ciphertext.
/// Version 1 CBC files remain readable for backward compatibility.
/// </summary>
public sealed class BackupEncryptionService : IBackupEncryptionService
{
    private const int IvSize = 16;

    private const int KeySizeBytes = 32; // 256 bits

    private const int Pbkdf2Iterations = 600_000;

    private const int SaltSize = 16;

    private const int GcmNonceSize = 12;

    private const int GcmTagSize = 16;

    private static readonly byte[] EncryptedMagic = "PGCLONE_ENC_V1"u8.ToArray();

    private static readonly byte[] EncryptedMagicV2 = "PGCLONE_ENC_V2"u8.ToArray();

    /// <inheritdoc />
    public bool IsEncrypted(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            var maxMagicLength = Math.Max(EncryptedMagic.Length, EncryptedMagicV2.Length);
            var buffer = new byte[maxMagicLength];
            var read = fs.Read(buffer, 0, buffer.Length);
            return (read >= EncryptedMagicV2.Length && buffer.AsSpan(0, EncryptedMagicV2.Length).SequenceEqual(EncryptedMagicV2))
                || (read >= EncryptedMagic.Length && buffer.AsSpan(0, EncryptedMagic.Length).SequenceEqual(EncryptedMagic));
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

        if (IsEncryptedV2Bytes(fileBytes))
            return DecryptV2(fileBytes, password);

        if (IsEncryptedV1Bytes(fileBytes))
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
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(GcmNonceSize);
        var key = DeriveKey(password, salt);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherText = new byte[plainBytes.Length];
        var tag = new byte[GcmTagSize];

        using (var aes = new AesGcm(key, GcmTagSize))
            aes.Encrypt(nonce, plainBytes, cipherText, tag);

        var output = new byte[EncryptedMagicV2.Length + SaltSize + GcmNonceSize + GcmTagSize + cipherText.Length];
        var offset = 0;
        EncryptedMagicV2.CopyTo(output, offset);
        offset += EncryptedMagicV2.Length;
        salt.CopyTo(output, offset);
        offset += salt.Length;
        nonce.CopyTo(output, offset);
        offset += nonce.Length;
        tag.CopyTo(output, offset);
        offset += tag.Length;
        cipherText.CopyTo(output, offset);

        var temporary = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(output);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private string DecryptV2(byte[] fileBytes, string? password)
    {
        if (string.IsNullOrEmpty(password))
            throw new CryptographicException("This backup file is encrypted. A password is required.");

        var offset = EncryptedMagicV2.Length;
        var salt = fileBytes[offset..(offset + SaltSize)];
        offset += SaltSize;
        var nonce = fileBytes[offset..(offset + GcmNonceSize)];
        offset += GcmNonceSize;
        var tag = fileBytes[offset..(offset + GcmTagSize)];
        offset += GcmTagSize;
        var cipherText = fileBytes[offset..];
        var plainBytes = new byte[cipherText.Length];

        using (var aes = new AesGcm(DeriveKey(password, salt), GcmTagSize))
            aes.Decrypt(nonce, cipherText, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
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

    private static bool IsEncryptedV2Bytes(byte[] fileBytes) =>
        fileBytes.Length >= EncryptedMagicV2.Length + SaltSize + GcmNonceSize + GcmTagSize &&
        fileBytes.AsSpan(0, EncryptedMagicV2.Length).SequenceEqual(EncryptedMagicV2);

    private static bool IsEncryptedV1Bytes(byte[] fileBytes) =>
        fileBytes.Length >= EncryptedMagic.Length + SaltSize + IvSize &&
        fileBytes.AsSpan(0, EncryptedMagic.Length).SequenceEqual(EncryptedMagic);
}
