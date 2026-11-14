// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text;

namespace StudyLife.Server.Services.SignIn;

/// <summary>
/// The login name of a password credential (docs/ARCHITECTURE.md "Password sign-in"). Pure rules,
/// no persistence: what a valid name is, how input is normalised, and the case-insensitive key the
/// unique index and the sign-in lookup use.
/// <list type="bullet">
/// <item>Normalisation: surrounding whitespace is trimmed and the text is brought into Unicode NFC,
/// so "é" typed as one code point or as "e" + combining accent is the same name.</item>
/// <item>Allowed: 3 to 64 characters, letters and digits of any script plus <c>. _ - @</c>
/// (an e-mail address works as a name), starting with a letter or a digit. No spaces, no control or
/// invisible characters.</item>
/// <item>Comparison: case-insensitive (<see cref="Key"/>, invariant lower case of the NFC form),
/// so "Alex" and "alex" are the same account name.</item>
/// </list>
/// </summary>
public static class UsernamePolicy
{
    public const int MinLength = 3;
    public const int MaxLength = 64;

    /// <summary>Trim + NFC. Returns "" for null/blank input and for text that is not valid
    /// Unicode (a lone surrogate cannot be normalised) - both then fail <see cref="IsValid"/>.</summary>
    public static string Normalize(string? input)
    {
        var trimmed = (input ?? "").Trim();
        if (trimmed.Length == 0) return "";
        try
        {
            return trimmed.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    /// <summary>True when an already <see cref="Normalize">normalised</see> name satisfies the rules.</summary>
    public static bool IsValid(string normalized)
    {
        if (normalized.Length is < MinLength or > MaxLength) return false;
        if (!char.IsLetterOrDigit(normalized, 0)) return false;
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '@') continue;
            // Letters outside the BMP arrive as surrogate pairs; char.IsLetterOrDigit(string, int)
            // classifies the whole pair.
            if (char.IsHighSurrogate(c) && i + 1 < normalized.Length && char.IsLetterOrDigit(normalized, i))
            {
                i++;
                continue;
            }
            return false;
        }
        return true;
    }

    /// <summary>The case-insensitive lookup key of a normalised name.</summary>
    public static string Key(string normalized) => normalized.ToLower(CultureInfo.InvariantCulture);
}
