// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace StudyLife.Server.Services;

/// <summary>
/// The one place the consent flows' PKCE rules (RFC 7636, S256 only) live: what a challenge
/// offer must look like, and how a verifier is checked against it. Used by the fixed-audience
/// connect actions (AuthController.5.Consent.cs) and the dynamic-client one (.10.OAuthClients.cs).
/// </summary>
public static partial class ConsentPkce
{
    public const string MethodS256 = "S256";

    public const string InvalidOfferMessage =
        "codeChallenge must be a base64url S256 challenge (43-128 characters) with codeChallengeMethod=S256.";

    /// <summary>RFC 7636 §4.1 unreserved-character shape shared by code_verifier and the base64url
    /// S256 challenge (43 = base64url of 32 bytes, 128 = the spec's upper bound).</summary>
    [GeneratedRegex("^[A-Za-z0-9\\-._~]{43,128}$")]
    private static partial Regex Shape();

    public static bool IsValidChallenge(string? challenge) => challenge is not null && Shape().IsMatch(challenge);

    /// <summary>What the caller sent: nothing, a well-formed S256 challenge, or something unusable
    /// (half a pair, a method other than S256, a malformed challenge).</summary>
    public enum OfferState { None, Valid, Invalid }

    public readonly record struct Offer(OfferState State, string? Challenge);

    public static Offer Evaluate(string? challenge, string? method)
    {
        var hasChallenge = !string.IsNullOrEmpty(challenge);
        var hasMethod = !string.IsNullOrEmpty(method);
        if (!hasChallenge && !hasMethod) return new Offer(OfferState.None, null);
        if (hasChallenge && hasMethod
            && string.Equals(method, MethodS256, StringComparison.Ordinal)
            && IsValidChallenge(challenge))
            return new Offer(OfferState.Valid, challenge);
        return new Offer(OfferState.Invalid, null);
    }

    /// <summary>base64url(SHA-256(verifier)) == challenge, compared in constant time.</summary>
    public static bool Matches(string challenge, string? verifier)
    {
        if (verifier is null || !Shape().IsMatch(verifier)) return false;
        var computed = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }
}
