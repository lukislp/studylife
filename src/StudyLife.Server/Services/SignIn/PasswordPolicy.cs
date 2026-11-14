// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text;

namespace StudyLife.Server.Services.SignIn;

/// <summary>Why a new password was refused. <see cref="Ok"/> = acceptable.</summary>
public enum PasswordRuleViolation
{
    Ok,
    TooShort,
    TooLong,
    /// <summary>On the built-in list of common passwords, or trivially repetitive/sequential.</summary>
    Common,
    /// <summary>Contains the account's own login name.</summary>
    ContainsUsername,
}

/// <summary>
/// Rules for a NEW password, following NIST SP 800-63B: length instead of composition rules (no
/// "one digit, one symbol"), a check against common passwords, no expiry, every character
/// including spaces and emoji allowed, paste and password managers welcome. Pure: no persistence,
/// no hashing.
///
/// Passwords are compared in Unicode NFKC (<see cref="Normalize"/>), as 800-63B recommends, so the
/// same password typed on two keyboards that encode an accent differently still matches. Length is
/// counted in Unicode scalar values (an emoji is one character, not two UTF-16 units).
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    /// <summary>Upper bound so a request cannot make the server hash megabytes; far above what
    /// anyone types and above the 64 that 800-63B asks verifiers to accept at least.</summary>
    public const int MaxLength = 128;

    /// <summary>NFKC form used for hashing and verification. Null for empty input and for text that
    /// is not valid Unicode (a lone surrogate) - such input can never be a password.</summary>
    public static string? Normalize(string? password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        try
        {
            return password.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Length in Unicode scalar values.</summary>
    public static int Length(string password) => password.EnumerateRunes().Count();

    /// <summary>Checks an already <see cref="Normalize">normalised</see> new password against the
    /// rules. <paramref name="normalizedUsername"/> is the login name it will belong to.</summary>
    public static PasswordRuleViolation Check(string? normalizedPassword, string? normalizedUsername)
    {
        if (string.IsNullOrEmpty(normalizedPassword)) return PasswordRuleViolation.TooShort;
        var length = Length(normalizedPassword);
        if (length < MinLength) return PasswordRuleViolation.TooShort;
        if (length > MaxLength) return PasswordRuleViolation.TooLong;
        if (CommonPasswords.IsCommon(normalizedPassword)) return PasswordRuleViolation.Common;
        // A three-letter name ("bob") inside a long passphrase is coincidence, not a weakness.
        if (normalizedUsername is { Length: >= 4 }
            && normalizedPassword.Contains(normalizedUsername, StringComparison.OrdinalIgnoreCase))
            return PasswordRuleViolation.ContainsUsername;
        return PasswordRuleViolation.Ok;
    }
}

/// <summary>
/// A small built-in blocklist (NIST 800-63B: "passwords obtained from previous breach corpuses,
/// dictionary words, repetitive or sequential characters"). Not a breach database - the server
/// works offline and must not send password material anywhere - but it stops the passwords every
/// guessing list starts with once they are long enough to pass the minimum length, plus anything
/// made of one short pattern repeated or a straight letter/digit run.
/// </summary>
public static class CommonPasswords
{
    // Lower case; compared case-insensitively. Only entries that reach the minimum length matter,
    // shorter ones already fail TooShort.
    private static readonly HashSet<string> List = new(StringComparer.Ordinal)
    {
        "123456789012", "1234567890123", "12345678901234", "123456789012345", "1234567890123456",
        "098765432109", "123123123123", "password1234", "password12345", "password123456",
        "password1234567", "passwordpassword", "password!234", "p@ssword1234", "p@ssw0rd1234",
        "passw0rd1234", "mypassword123", "qwertyuiop12", "qwertyuiop123", "qwertyuiopasdf",
        "qwertyuiopasdfgh", "qwerty123456", "qwertz123456", "qwertzuiop12", "asdfghjkl123",
        "azertyuiop12", "1q2w3e4r5t6y", "1q2w3e4r5t6y7u", "q1w2e3r4t5y6", "zaq12wsxcde3",
        "1qaz2wsx3edc", "iloveyou1234", "iloveyou12345", "letmein12345", "welcome12345",
        "welcome123456", "abc123456789", "abcdef123456", "123456abcdef", "123456789abc",
        "monkey123456", "dragon123456", "football1234", "baseball1234", "sunshine1234",
        "princess1234", "superman1234", "batman123456", "starwars1234", "trustno11234",
        "changeme1234", "changeme12345", "administrator", "administrator1", "admin1234567",
        "adminadmin12", "rootroot1234", "studylife123", "studylife1234", "studylife2026",
        "passwort1234", "passwort12345", "hallo1234567", "hallohallo12", "geheim123456",
        "motdepasse12", "contraseña12", "contrasena12", "wachtwoord12", "salasana1234",
        "lösenord1234", "losenord1234", "haslo1234567", "heslo1234567", "parola123456",
        "jelszo123456", "geslo1234567", "lozinka12345", "parool123456", "slaptazodis1",
        "correcthorsebatterystaple", "thequickbrownfox", "mustang12345", "michael12345",
        "jennifer1234", "charlie12345", "computer1234", "internet1234", "whatever1234",
    };

    public static bool IsCommon(string normalizedPassword)
    {
        var lower = normalizedPassword.ToLower(CultureInfo.InvariantCulture);
        return List.Contains(lower) || IsRepetition(lower) || IsRun(lower);
    }

    /// <summary>The whole password is one pattern of up to four characters repeated
    /// ("aaaaaaaaaaaa", "abababababab", "abcabcabcabc", "12341234123412").</summary>
    private static bool IsRepetition(string s)
    {
        for (var period = 1; period <= 4 && period < s.Length; period++)
        {
            var repeats = true;
            for (var i = period; i < s.Length && repeats; i++)
                repeats = s[i] == s[i - period];
            if (repeats) return true;
        }
        return false;
    }

    /// <summary>One straight run up or down ("abcdefghijklm", "zyxwvutsrqpo").</summary>
    private static bool IsRun(string s)
    {
        if (s.Length < 2) return false;
        var step = s[1] - s[0];
        if (step is not (1 or -1)) return false;
        for (var i = 2; i < s.Length; i++)
            if (s[i] - s[i - 1] != step) return false;
        return true;
    }
}
