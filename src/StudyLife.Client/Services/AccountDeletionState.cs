// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudyLife.Shared;

namespace StudyLife.Client.Services;

/// <summary>Why a deletion request did not go through, as far as the UI has to tell them apart.</summary>
public enum AccountDeletionFailure
{
    None,
    /// <summary>The passkey assertion or recovery code was not accepted (403 reauthentication-failed).</summary>
    ProofRejected,
    /// <summary>The server wants an explicit proof and none was sent (400).</summary>
    ProofMissing,
    /// <summary>The owner cannot leave while other accounts exist (409).</summary>
    OwnerHasOtherUsers,
    OwnerOnly,
    Demo,
    NotFound,
    /// <summary>Too many attempts (429): wait before trying again.</summary>
    TooManyAttempts,
    Network,
    Other,
}

/// <summary>What the user proved right before deletion: a passkey assertion for the challenge the
/// server issued, or one of their recovery codes.</summary>
public sealed record AccountDeletionProof(string? OptionsId, string? AssertionJson, string? RecoveryCode);

public sealed record AccountDeletionAttempt(AccountDeletionFailure Failure, AccountDeletionResultDto? Result = null)
{
    public bool Succeeded => Failure == AccountDeletionFailure.None;
}

/// <summary>
/// The client half of "delete your own account" (App Store guideline 5.1.1(v)): the API calls and
/// the local clean-up, nothing else - what is deleted, who may delete and what counts as proof is
/// decided by the server (AccountDeletionService). The component that shows the danger card only
/// renders this state and runs the passkey prompt, because that needs the page's JS module.
///
/// Held by AppStateService (<c>State.AccountDeletion</c>) instead of being a DI registration of its
/// own, like <see cref="AiConsentState"/>: the native apps compile this client into their bundle but
/// build their own service container, so a new registration here would have to be mirrored there.
/// </summary>
public sealed class AccountDeletionState
{
    private readonly HttpClient _http;
    private readonly SessionTokenStore _tokens;

    public AccountDeletionState(HttpClient http, SessionTokenStore tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    /// <summary>What deleting would do, or why it is not possible. Null when the server cannot be reached.</summary>
    public async Task<AccountDeletionPreviewDto?> GetPreviewAsync()
    {
        try { return await _http.GetFromJsonAsync<AccountDeletionPreviewDto>("api/auth/account/deletion-preview"); }
        catch { return null; }
    }

    /// <summary>The owner's list of other accounts (null for anyone else, or when unreachable).</summary>
    public async Task<List<UserListItemDto>?> ListUsersAsync()
    {
        try
        {
            var response = await _http.GetAsync("api/auth/users");
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<UserListItemDto>>() : null;
        }
        catch { return null; }
    }

    /// <summary>The passkey challenge for the re-authentication. Null when the account has no passkey
    /// (the UI then offers a recovery code) or the server cannot be reached.</summary>
    public async Task<PasskeyBeginResponseDto?> BeginPasskeyProofAsync()
    {
        try
        {
            var response = await _http.PostAsync("api/auth/account/reauth/begin", null);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<PasskeyBeginResponseDto>() : null;
        }
        catch { return null; }
    }

    /// <summary>Deletes the signed-in account. On success the session is gone on the server, so the local
    /// token and every account-scoped cache are cleared here too - the caller then navigates away.</summary>
    public async Task<AccountDeletionAttempt> DeleteOwnAccountAsync(AccountDeletionProof proof)
    {
        var attempt = await SendAsync("api/auth/account", proof);
        // The same purge a logout runs (token, read caches, offline write queue, change stream).
        if (attempt.Succeeded) await _tokens.ClearAsync();
        return attempt;
    }

    /// <summary>Owner only: removes another account through the same server use case.</summary>
    public Task<AccountDeletionAttempt> RemoveUserAsync(int userId, AccountDeletionProof proof) =>
        SendAsync($"api/auth/users/{userId}", proof);

    private async Task<AccountDeletionAttempt> SendAsync(string url, AccountDeletionProof proof)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, url)
            {
                Content = new StringContent(BuildBody(proof), Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
                return new AccountDeletionAttempt(AccountDeletionFailure.None,
                    await response.Content.ReadFromJsonAsync<AccountDeletionResultDto>());
            return new AccountDeletionAttempt(await ClassifyAsync(response));
        }
        catch (HttpRequestException)
        {
            return new AccountDeletionAttempt(AccountDeletionFailure.Network);
        }
        catch (TaskCanceledException)
        {
            return new AccountDeletionAttempt(AccountDeletionFailure.Network);
        }
    }

    internal static string BuildBody(AccountDeletionProof proof)
    {
        var body = new JsonObject();
        if (!string.IsNullOrWhiteSpace(proof.RecoveryCode)) body["recoveryCode"] = proof.RecoveryCode.Trim();
        if (proof.OptionsId is not null && proof.AssertionJson is not null)
        {
            body["optionsId"] = proof.OptionsId;
            body["response"] = JsonNode.Parse(proof.AssertionJson);
        }
        return body.ToJsonString();
    }

    internal static async Task<AccountDeletionFailure> ClassifyAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return AccountDeletionFailure.TooManyAttempts;
        if (response.StatusCode == HttpStatusCode.NotFound) return AccountDeletionFailure.NotFound;
        if (response.StatusCode == HttpStatusCode.BadRequest) return AccountDeletionFailure.ProofMissing;

        // 403/409 carry an RFC 9457 problem whose "type" is a stable code.
        string? type = null;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("type", out var t)) type = t.GetString();
        }
        catch (JsonException)
        {
            // No problem body: falls through below.
        }

        return type switch
        {
            "reauthentication-failed" => AccountDeletionFailure.ProofRejected,
            "owner-has-other-users" => AccountDeletionFailure.OwnerHasOtherUsers,
            "owner-only" => AccountDeletionFailure.OwnerOnly,
            "demo-readonly" => AccountDeletionFailure.Demo,
            // The demo write block answers a plain 403 without a problem type.
            _ => response.StatusCode == HttpStatusCode.Forbidden && type is null
                ? AccountDeletionFailure.Demo
                : AccountDeletionFailure.Other,
        };
    }
}
