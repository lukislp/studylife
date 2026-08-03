using System.Security.Cryptography;
using System.Text;

namespace StudyLife.Server.Services;

/// <summary>
/// Thrown on a wrong password or a tampered/corrupted encrypted
/// backup file. AES-GCM's auth tag covers both - a wrong password leads to
/// a wrongly derived key, a tampered file to a no-longer-matching
/// tag; both fail the same check and are indistinguishable from the outside. A dedicated
/// exception type so the controller can cleanly separate this from an unexpected error and
/// return a clear 400 message instead of a raw 500.
/// </summary>
public sealed class BackupDecryptionException : Exception
{
    public BackupDecryptionException(string message) : base(message) { }
}

/// <summary>
/// Optional password encryption for downloaded .db backups (SetupBackupCard /
/// SetupRestoreCard). Deliberately no NuGet package for this - AES-GCM (authenticated encryption) and
/// PBKDF2 (Rfc2898DeriveBytes) from System.Security.Cryptography are entirely sufficient for this purpose.
/// The password itself is never persisted anywhere, only used at runtime from the request.
///
/// File format (raw bytes, no framing library needed):
///   [Magic 8 bytes: "SLBKENC1" ASCII][Salt 16 bytes][Nonce 12 bytes][Tag 16 bytes][Ciphertext N bytes]
///
/// AES-GCM instead of AES-CBC: authenticated encryption - the 16-byte tag detects both a
/// wrong password and a subsequently tampered/corrupted file. AES-CBC alone
/// would silently let both through as garbage data and would need a separate HMAC
/// in addition for the same guarantee - AesGcm covers this with no extra effort.
///
/// PBKDF2-HMAC-SHA256 with 210,000 iterations (OWASP recommendation, as of 2023) instead of
/// using the password directly or unhardened as the key - makes brute force against an
/// exfiltrated backup noticeably more expensive. Salt is freshly randomly drawn per encryption
/// and never reused, just like the nonce.
/// </summary>
public static class BackupEncryptionService
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SLBKENC1");
    private const int SaltSize = 16;
    private const int NonceSize = 12; // AES-GCM standard nonce size, see AesGcm.NonceByteSizes
    private const int TagSize = 16;   // AES-GCM standard tag size, see AesGcm.TagByteSizes
    private const int KeySize = 32;   // AES-256
    private const int Pbkdf2Iterations = 210_000;
    private static readonly HashAlgorithmName Pbkdf2Hash = HashAlgorithmName.SHA256;

    private static readonly int HeaderSize = Magic.Length + SaltSize + NonceSize + TagSize;

    /// <summary>
    /// Checks only the magic header at the start of the file, without reading the rest of the
    /// file - cheap enough to be called before every restore upload.
    /// </summary>
    public static bool IsEncrypted(ReadOnlySpan<byte> data)
        => data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic);

    /// <summary>Like <see cref="IsEncrypted(ReadOnlySpan{byte})"/>, but only reads the first bytes from disk.</summary>
    public static bool IsEncryptedFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < Magic.Length) return false;
        var buffer = new byte[Magic.Length];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return read == Magic.Length && IsEncrypted(buffer);
    }

    /// <summary>Encrypts <paramref name="plaintext"/> with a key derived from <paramref name="password"/>.</summary>
    public static byte[] Encrypt(byte[] plaintext, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, Pbkdf2Hash, KeySize);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aesGcm = new AesGcm(key, TagSize))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var output = new byte[HeaderSize + ciphertext.Length];
        var offset = 0;
        Magic.CopyTo(output, offset); offset += Magic.Length;
        salt.CopyTo(output, offset); offset += SaltSize;
        nonce.CopyTo(output, offset); offset += NonceSize;
        tag.CopyTo(output, offset); offset += TagSize;
        ciphertext.CopyTo(output, offset);
        return output;
    }

    /// <summary>
    /// Decrypts a backup produced by <see cref="Encrypt"/>. Throws
    /// <see cref="BackupDecryptionException"/> on a wrong password, a missing/broken header,
    /// or a truncated/tampered file - never a raw CryptographicException or
    /// an index exception, so the controller can always return a clear 400 response.
    /// </summary>
    public static byte[] Decrypt(byte[] data, string password)
    {
        if (data.Length < HeaderSize || !IsEncrypted(data))
            throw new BackupDecryptionException("Not a StudyLife encrypted backup (missing or invalid header).");

        var offset = Magic.Length;
        var salt = data.AsSpan(offset, SaltSize).ToArray(); offset += SaltSize;
        var nonce = data.AsSpan(offset, NonceSize).ToArray(); offset += NonceSize;
        var tag = data.AsSpan(offset, TagSize).ToArray(); offset += TagSize;
        var ciphertext = data.AsSpan(offset).ToArray();

        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, Pbkdf2Hash, KeySize);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            throw new BackupDecryptionException(
                "Wrong password, or the file is corrupted / not a valid encrypted StudyLife backup.");
        }

        return plaintext;
    }
}
