using System.Text;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Pure unit tests for BackupEncryptionService - no host/DB dependency, tests only the
/// encryption format itself (round trip, wrong password, corrupted/truncated files).
/// The integration with the restore upload endpoint (including the encrypted:true flag) is
/// covered separately in BackupControllerEncryptedTests.
/// </summary>
public class BackupEncryptionServiceTests
{
    private static byte[] SamplePlaintext(int length = 4096)
    {
        // Deterministic instead of real randomness, so a failing test stays reproducible.
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    [Fact]
    public void EncryptThenDecrypt_RoundTrip_ProducesOriginalBytes()
    {
        var plaintext = SamplePlaintext();

        var encrypted = BackupEncryptionService.Encrypt(plaintext, "correct horse battery staple");
        var decrypted = BackupEncryptionService.Decrypt(encrypted, "correct horse battery staple");

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptThenDecrypt_EmptyPlaintext_RoundTrips()
    {
        // Edge case: a 0-byte file must not fall below the header length and still needs to be
        // decrypted/encrypted cleanly (no off-by-one in the ciphertext length).
        var encrypted = BackupEncryptionService.Encrypt([], "pw");
        var decrypted = BackupEncryptionService.Decrypt(encrypted, "pw");

        Assert.Empty(decrypted);
    }

    [Fact]
    public void Encrypt_IsNonDeterministic_DifferentSaltAndNonceEachCall()
    {
        // Fresh salt/nonce per call - two encryptions of the same plaintext with the same
        // password must NOT produce the same bytes (otherwise the nonce would be reused,
        // which breaks AES-GCM's security guarantee).
        var plaintext = SamplePlaintext(64);

        var first = BackupEncryptionService.Encrypt(plaintext, "pw");
        var second = BackupEncryptionService.Encrypt(plaintext, "pw");

        Assert.NotEqual(first, second);
        // But both must still yield the same plaintext.
        Assert.Equal(plaintext, BackupEncryptionService.Decrypt(first, "pw"));
        Assert.Equal(plaintext, BackupEncryptionService.Decrypt(second, "pw"));
    }

    [Fact]
    public void IsEncrypted_TrueForEncryptedOutput_FalseForPlainSqliteHeader()
    {
        var encrypted = BackupEncryptionService.Encrypt(SamplePlaintext(64), "pw");
        Assert.True(BackupEncryptionService.IsEncrypted(encrypted));

        // Real SQLite file header (see DatabaseRestoreServiceTests/BackupControllerTests) -
        // must not be falsely detected as "encrypted".
        var sqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
        Assert.False(BackupEncryptionService.IsEncrypted(sqliteHeader));

        Assert.False(BackupEncryptionService.IsEncrypted([]));
        Assert.False(BackupEncryptionService.IsEncrypted([1, 2, 3]));
    }

    [Fact]
    public void IsEncryptedFile_ReadsOnlyTheHeader_MatchesInMemoryCheck()
    {
        var encryptedPath = Path.Combine(Path.GetTempPath(), $"studylife-enc-test-{Guid.NewGuid():N}.db.enc");
        var plainPath = Path.Combine(Path.GetTempPath(), $"studylife-plain-test-{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllBytes(encryptedPath, BackupEncryptionService.Encrypt(SamplePlaintext(64), "pw"));
            File.WriteAllBytes(plainPath, Encoding.ASCII.GetBytes("SQLite format 3\0dummy"));

            Assert.True(BackupEncryptionService.IsEncryptedFile(encryptedPath));
            Assert.False(BackupEncryptionService.IsEncryptedFile(plainPath));
        }
        finally
        {
            foreach (var p in new[] { encryptedPath, plainPath })
                try { File.Delete(p); } catch (IOException) { }
        }
    }

    [Fact]
    public void Decrypt_WrongPassword_ThrowsBackupDecryptionException_NotARawCrash()
    {
        var encrypted = BackupEncryptionService.Encrypt(SamplePlaintext(), "correct-password");

        var ex = Assert.Throws<BackupDecryptionException>(
            () => BackupEncryptionService.Decrypt(encrypted, "wrong-password"));
        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public void Decrypt_TruncatedFile_ThrowsBackupDecryptionException_NotAnIndexException()
    {
        var encrypted = BackupEncryptionService.Encrypt(SamplePlaintext(), "pw");
        var truncated = encrypted.Take(encrypted.Length / 2).ToArray();

        Assert.Throws<BackupDecryptionException>(() => BackupEncryptionService.Decrypt(truncated, "pw"));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsBackupDecryptionException()
    {
        // The GCM auth tag must detect a tampered-with file after the fact, not just a wrong
        // password - flip one byte in the middle of the ciphertext part (after the header).
        var encrypted = BackupEncryptionService.Encrypt(SamplePlaintext(256), "pw");
        var tampered = (byte[])encrypted.Clone();
        tampered[^1] ^= 0xFF;

        Assert.Throws<BackupDecryptionException>(() => BackupEncryptionService.Decrypt(tampered, "pw"));
    }

    [Fact]
    public void Decrypt_NotEncryptedData_ThrowsBackupDecryptionException_NoMagicHeader()
    {
        var plainSqliteLikeBytes = Encoding.ASCII.GetBytes("SQLite format 3\0" + new string('x', 200));

        Assert.Throws<BackupDecryptionException>(
            () => BackupEncryptionService.Decrypt(plainSqliteLikeBytes, "pw"));
    }

    [Fact]
    public void Decrypt_EmptyInput_ThrowsBackupDecryptionException()
    {
        Assert.Throws<BackupDecryptionException>(() => BackupEncryptionService.Decrypt([], "pw"));
    }
}
