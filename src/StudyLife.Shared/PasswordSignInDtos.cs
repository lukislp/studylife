// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel.DataAnnotations;

namespace StudyLife.Shared;

/// <summary>
/// GET /api/auth/methods (anonymous): which sign-in methods this installation offers, so the sign-in
/// and registration pages know whether to show the username + password form. Passkeys are always
/// offered and therefore not listed.
/// </summary>
public class AuthMethodsDto
{
    /// <summary>The operator switch <c>PasswordSignIn:Enabled</c> (default on).</summary>
    public bool PasswordSignIn { get; set; }
}

/// <summary>POST /api/auth/password/login. The wire limits are generous on purpose - the real
/// rules (UsernamePolicy/PasswordPolicy on the server) only answer "wrong" for anything else, so
/// the limits never tell a caller more than a wrong password would.</summary>
public class PasswordLoginRequestDto
{
    [MaxLength(256)]
    public string? Username { get; set; }

    [MaxLength(512)]
    public string? Password { get; set; }
}

/// <summary>Successful POST /api/auth/password/login: the same session token a passkey sign-in
/// returns, plus whether the password is a temporary one the owner set (the client then sends the
/// user to Settings to choose their own).</summary>
public class PasswordLoginResponseDto : PasskeyCompleteResponseDto
{
    public bool PasswordChangeRequired { get; set; }
}

/// <summary>POST /api/auth/password/register: a new account that signs in with a username and
/// password instead of a passkey. Same gates as the passkey registration (setup code on a fresh
/// installation, Registration:Mode and invites afterwards).</summary>
public class PasswordRegisterRequestDto
{
    [MaxLength(100)]
    public string DisplayName { get; set; } = "";

    [MaxLength(256)]
    public string? Username { get; set; }

    [MaxLength(512)]
    public string? Password { get; set; }

    [MaxLength(200)]
    public string? SetupSecret { get; set; }

    [MaxLength(500)]
    public string? InviteToken { get; set; }
}

/// <summary>GET /api/auth/password: the password card in Settings.</summary>
public class PasswordStatusDto
{
    /// <summary>The instance switch. When false the card only explains that passwords are off.</summary>
    public bool Enabled { get; set; }

    public bool HasPassword { get; set; }

    /// <summary>The login name, null while the account has no password.</summary>
    public string? Username { get; set; }

    public DateTime? UpdatedAt { get; set; }

    /// <summary>The owner set a temporary password; the user should choose their own.</summary>
    public bool ChangeRequired { get; set; }

    /// <summary>False when the password is the account's only usable sign-in method, so removing
    /// it would lock the account out (add a passkey first).</summary>
    public bool CanRemove { get; set; }

    /// <summary>True when the account has at least one approved passkey (the card then offers the
    /// passkey as the confirmation for changes).</summary>
    public bool HasPasskey { get; set; }
}

/// <summary>POST /api/auth/users/{id}/password-reset (owner only): the temporary password, shown to
/// the owner exactly once - only its hash is stored.</summary>
public class PasswordResetResultDto
{
    public string Username { get; set; } = "";
    public string TemporaryPassword { get; set; } = "";
}
