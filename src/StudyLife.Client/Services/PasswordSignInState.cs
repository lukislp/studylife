// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudyLife.Shared;

namespace StudyLife.Client.Services;

/// <summary>Why a password request did not go through, as far as the UI has to tell them apart.
/// Mirrors the server's stable problem types (AuthController.12.Password.cs).</summary>
public enum PasswordFailure
{
    None,
    /// <summary>Sign-in refused: unknown name, wrong password or a temporary lockout - the server
    /// deliberately does not say which.</summary>
    InvalidCredentials,
    TooManyAttempts,
    Disabled,
    UsernameInvalid,
    UsernameTaken,
    /// <summary>The account already has a password, so its login name cannot change.</summary>
    UsernameFixed,
    TooShort,
    TooLong,
    Common,
    ContainsUsername,
    LastMethod,
    ProofRequired,
    ProofRejected,
    /// <summary>Registration on a fresh installation without (or with a wrong) setup code.</summary>
    SetupSecretInvalid,
    /// <summary>Registration refused by Registration:Mode; <see cref="PasswordAttempt.GateReason"/> says why.</summary>
    RegistrationGate,
    OwnerOnly,
    Network,
    Other,
}

/// <summary>The answer to "does this installation offer password sign-in?".</summary>
public enum PasswordOffer
{
    /// <summary>Could not ask (no network, server unreachable or failing): ask again, do not cache.</summary>
    Unknown,
    /// <summary>The server answered: password sign-in is switched off (or the server predates it).</summary>
    NotOffered,
    Offered,
}

/// <summary>Outcome of a password request. <see cref="Token"/> is set after a successful sign-in or
/// registration (the plaintext session token, exactly like a passkey sign-in returns it).</summary>
public sealed record PasswordAttempt(PasswordFailure Failure, string? Token = null, bool ChangeRequired = false,
    string? GateReason = null, PasswordResetResultDto? Reset = null)
{
    public bool Succeeded => Failure == PasswordFailure.None;
}

/// <summary>The fresh proof a password change, removal or owner reset carries: the current password,
/// a recovery code, or a passkey assertion for the challenge of account/reauth/begin. All empty =
/// rely on a sign-in within the last two minutes.</summary>
public sealed record PasswordProof(string? CurrentPassword = null, string? RecoveryCode = null,
    string? OptionsId = null, string? AssertionJson = null);

/// <summary>
/// The client half of username + password sign-in (docs/ARCHITECTURE.md "Password sign-in"): the API
/// calls and the mapping of the server's answers, nothing else - every rule is decided on the server.
/// Stateless, so the public sign-in pages simply construct one over their HttpClient; Setup reaches
/// it through AppStateService (<c>State.PasswordSignIn</c>), like <see cref="AccountDeletionState"/>,
/// so the native apps need no extra registration in their own service container. It never stores a
/// password: the browser or the OS password manager does that, and only the session token ends up
/// in <see cref="SessionTokenStore"/>.
/// </summary>
public sealed class PasswordSignInState(HttpClient http)
{
    /// <summary>Whether the installation offers password sign-in, as far as the server could be asked.
    /// <see cref="PasswordOffer.Unknown"/> (offline, server down, a captive portal's page) is deliberately
    /// not "not offered": pages keep asking (<see cref="Reasker"/>) instead of hiding the form for good.
    /// An older server without the endpoint (404) does answer: not offered.</summary>
    public async Task<PasswordOffer> GetOfferAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync("api/auth/methods", ct);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Gone)
                return PasswordOffer.NotOffered;
            if (!response.IsSuccessStatusCode) return PasswordOffer.Unknown;
            var dto = await response.Content.ReadFromJsonAsync<AuthMethodsDto>(ct);
            if (dto is null) return PasswordOffer.Unknown;
            return dto.PasswordSignIn ? PasswordOffer.Offered : PasswordOffer.NotOffered;
        }
        catch (HttpRequestException) { return PasswordOffer.Unknown; }
        catch (JsonException) { return PasswordOffer.Unknown; }
        catch (NotSupportedException) { return PasswordOffer.Unknown; } // a non-JSON body
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return PasswordOffer.Unknown; } // timeout
    }

    public Task<PasswordAttempt> SignInAsync(string username, string password) =>
        SendForTokenAsync("api/auth/password/login",
            JsonContent.Create(new PasswordLoginRequestDto { Username = username, Password = password }),
            unauthorized: PasswordFailure.InvalidCredentials);

    public Task<PasswordAttempt> RegisterAsync(string displayName, string username, string password, string? setupSecret, string? inviteToken) =>
        SendForTokenAsync("api/auth/password/register",
            JsonContent.Create(new PasswordRegisterRequestDto
            {
                DisplayName = displayName,
                Username = username,
                Password = password,
                SetupSecret = string.IsNullOrWhiteSpace(setupSecret) ? null : setupSecret.Trim(),
                InviteToken = inviteToken,
            }),
            unauthorized: PasswordFailure.SetupSecretInvalid);

    /// <summary>The password card's state; null when the server cannot be reached.</summary>
    public async Task<PasswordStatusDto?> GetStatusAsync()
    {
        try
        {
            var response = await http.GetAsync("api/auth/password");
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<PasswordStatusDto>() : null;
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    /// <summary>Adds or changes the password. Only the first password chooses the login name; for a change pass null (the server refuses a different name).</summary>
    public Task<PasswordAttempt> SetAsync(string? username, string newPassword, PasswordProof proof)
    {
        var body = BuildProof(proof);
        if (!string.IsNullOrWhiteSpace(username)) body["username"] = username.Trim();
        body["newPassword"] = newPassword;
        return SendAsync(HttpMethod.Put, "api/auth/password", body);
    }

    public Task<PasswordAttempt> RemoveAsync(PasswordProof proof) =>
        SendAsync(HttpMethod.Delete, "api/auth/password", BuildProof(proof));

    /// <summary>Owner only: a temporary password for another account (<see cref="PasswordAttempt.Reset"/>).</summary>
    public async Task<PasswordAttempt> ResetUserAsync(int userId, string? username, PasswordProof proof)
    {
        var body = BuildProof(proof);
        if (!string.IsNullOrWhiteSpace(username)) body["username"] = username.Trim();
        try
        {
            using var response = await http.PostAsync($"api/auth/users/{userId}/password-reset",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode) return new PasswordAttempt(await ClassifyAsync(response));
            return new PasswordAttempt(PasswordFailure.None, Reset: await response.Content.ReadFromJsonAsync<PasswordResetResultDto>());
        }
        catch (HttpRequestException) { return new PasswordAttempt(PasswordFailure.Network); }
        catch (TaskCanceledException) { return new PasswordAttempt(PasswordFailure.Network); }
    }

    internal static JsonObject BuildProof(PasswordProof proof)
    {
        var body = new JsonObject();
        if (!string.IsNullOrEmpty(proof.CurrentPassword)) body["currentPassword"] = proof.CurrentPassword;
        if (!string.IsNullOrWhiteSpace(proof.RecoveryCode)) body["recoveryCode"] = proof.RecoveryCode.Trim();
        if (proof.OptionsId is not null && proof.AssertionJson is not null)
        {
            body["optionsId"] = proof.OptionsId;
            body["response"] = JsonNode.Parse(proof.AssertionJson);
        }
        return body;
    }

    private async Task<PasswordAttempt> SendAsync(HttpMethod method, string url, JsonObject body)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(request);
            return response.IsSuccessStatusCode
                ? new PasswordAttempt(PasswordFailure.None)
                : new PasswordAttempt(await ClassifyAsync(response));
        }
        catch (HttpRequestException) { return new PasswordAttempt(PasswordFailure.Network); }
        catch (TaskCanceledException) { return new PasswordAttempt(PasswordFailure.Network); }
    }

    /// <summary>Sign-in and registration: both answer with a session token. A 401 means
    /// <paramref name="unauthorized"/> - wrong credentials for the sign-in, a missing or wrong
    /// setup code for the registration.</summary>
    private async Task<PasswordAttempt> SendForTokenAsync(string url, HttpContent content, PasswordFailure unauthorized)
    {
        try
        {
            using var response = await http.PostAsync(url, content);
            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<PasswordLoginResponseDto>();
                return dto?.Token is { Length: > 0 } token
                    ? new PasswordAttempt(PasswordFailure.None, token, dto.PasswordChangeRequired)
                    : new PasswordAttempt(PasswordFailure.Other);
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new PasswordAttempt(unauthorized);
            if (response.StatusCode == HttpStatusCode.Forbidden && await GateReasonAsync(response) is { } reason)
                return new PasswordAttempt(PasswordFailure.RegistrationGate, GateReason: reason);
            return new PasswordAttempt(await ClassifyAsync(response));
        }
        catch (HttpRequestException) { return new PasswordAttempt(PasswordFailure.Network); }
        catch (TaskCanceledException) { return new PasswordAttempt(PasswordFailure.Network); }
        catch (JsonException) { return new PasswordAttempt(PasswordFailure.Other); }
    }

    /// <summary>The registration gate answers 403 with { reason } instead of a problem type.</summary>
    private static async Task<string?> GateReasonAsync(HttpResponseMessage response)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    internal static async Task<PasswordFailure> ClassifyAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return PasswordFailure.TooManyAttempts;

        string? type = null;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out var t))
                type = t.GetString();
        }
        catch (JsonException)
        {
            // Plain-text or empty body: decided by the status code below.
        }

        return type switch
        {
            "password-sign-in-disabled" => PasswordFailure.Disabled,
            "username-invalid" => PasswordFailure.UsernameInvalid,
            "username-taken" => PasswordFailure.UsernameTaken,
            "username-fixed" => PasswordFailure.UsernameFixed,
            "password-too-short" => PasswordFailure.TooShort,
            "password-too-long" => PasswordFailure.TooLong,
            "password-common" => PasswordFailure.Common,
            "password-contains-username" => PasswordFailure.ContainsUsername,
            "last-sign-in-method" => PasswordFailure.LastMethod,
            "reauthentication-required" => PasswordFailure.ProofRequired,
            "reauthentication-failed" => PasswordFailure.ProofRejected,
            "owner-only" => PasswordFailure.OwnerOnly,
            _ => response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => PasswordFailure.InvalidCredentials,
                // The demo instance's write block answers a plain 403.
                HttpStatusCode.Forbidden => PasswordFailure.Disabled,
                _ => PasswordFailure.Other,
            },
        };
    }

    /// <summary>The message for a failure from the given text table.</summary>
    public static string Message(PasswordFailure failure, StudyLife.Client.I18nText.PasswordText t) => failure switch
    {
        PasswordFailure.InvalidCredentials => t.LoginFailed,
        PasswordFailure.TooManyAttempts => t.ErrorTooMany,
        PasswordFailure.Disabled => t.ErrorDisabled,
        PasswordFailure.UsernameInvalid => t.ErrorUsernameInvalid,
        PasswordFailure.UsernameTaken => t.ErrorUsernameTaken,
        PasswordFailure.UsernameFixed => t.ErrorUsernameFixed,
        PasswordFailure.TooShort => t.ErrorTooShort,
        PasswordFailure.TooLong => t.ErrorTooLong,
        PasswordFailure.Common => t.ErrorCommon,
        PasswordFailure.ContainsUsername => t.ErrorContainsUsername,
        PasswordFailure.LastMethod => t.ErrorLastMethod,
        PasswordFailure.ProofRequired => t.ErrorProofRequired,
        PasswordFailure.ProofRejected => t.ErrorProofRejected,
        PasswordFailure.Network => t.ErrorNetwork,
        _ => t.ErrorGeneric,
    } ?? "";
}
