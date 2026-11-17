// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;

namespace StudyLife.Server.Services;

/// <summary>
/// One recovery code: 16 characters from a 32-character alphabet (80 bits, CSPRNG; the alphabet
/// size divides 2^32, so GetInt32 has no bias) without 0/O/1/I, since codes are typed in from
/// paper. Shown as XXXX-XXXX-XXXX-XXXX; the stored hash covers the raw characters only.
///
/// 80 bits (audit 2026-11-16 L4, was 60): the codes are stored as unsalted SHA-256, which is
/// fine only while brute-forcing the code space offline (from a leaked database) stays out of
/// reach. Codes generated before this change keep working - redemption hashes whatever was typed
/// in, whatever its length - and are replaced the next time the user regenerates their codes.
/// </summary>
public static class RecoveryCodeGenerator
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 16;
    private const int GroupSize = 4;

    /// <summary>A fresh code: <c>Raw</c> is what gets hashed, <c>Display</c> what the user sees.</summary>
    public static (string Raw, string Display) Next()
    {
        var raw = string.Concat(Enumerable.Range(0, Length)
            .Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]));
        var display = string.Join('-', Enumerable.Range(0, Length / GroupSize)
            .Select(i => raw.Substring(i * GroupSize, GroupSize)));
        return (raw, display);
    }
}
