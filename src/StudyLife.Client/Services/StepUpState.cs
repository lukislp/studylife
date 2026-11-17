// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Text;
using System.Text.Json;

namespace StudyLife.Client.Services;

/// <summary>How a "confirm it's you" attempt ended, as far as the dialog has to tell them apart.</summary>
public enum StepUpOutcome
{
    Confirmed,
    /// <summary>The proof was not accepted (403 reauthentication-failed).</summary>
    Rejected,
    /// <summary>No proof was sent and the session did not just sign in (400).</summary>
    Missing,
    /// <summary>Too many attempts (429).</summary>
    TooManyAttempts,
    Network,
    Other,
}

/// <summary>
/// The client half of the server's step-up (2026-11-16 audit, finding M1): approving or removing a
/// passkey, generating recovery codes, signing out the other devices and the raw database download /
/// restore answer 403 <c>step-up-required</c> unless this session confirmed its user moments ago.
/// A host runs its request; when <see cref="IsStepUpRequiredAsync"/> says so, it shows the
/// StepUpDialog (FreshProofPicker inside), which sends the proof through <see cref="ConfirmAsync"/>,
/// and then repeats the request.
///
/// Held by AppStateService (<c>State.StepUp</c>) instead of being a DI registration of its own, for
/// the same reason as <see cref="AccountDeletionState"/>: the native apps build their own container.
/// </summary>
public sealed class StepUpState
{
    /// <summary>Problem type of a refused guarded request (RequireStepUpAttribute on the server).</summary>
    public const string StepUpRequiredType = "step-up-required";

    /// <summary>Problem type of a passkey approval from the session that registered it.</summary>
    public const string ApprovalNeedsOtherSessionType = "approval-needs-other-session";

    private readonly HttpClient _http;

    public StepUpState(HttpClient http) => _http = http;

    /// <summary>Sends the proof; on success the server treats this session as confirmed for a few minutes.</summary>
    public async Task<StepUpOutcome> ConfirmAsync(FreshProof proof)
    {
        try
        {
            using var content = new StringContent(proof.ToRequestJson(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("api/auth/account/reauth", content);
            return response.StatusCode switch
            {
                _ when response.IsSuccessStatusCode => StepUpOutcome.Confirmed,
                HttpStatusCode.BadRequest => StepUpOutcome.Missing,
                HttpStatusCode.TooManyRequests => StepUpOutcome.TooManyAttempts,
                HttpStatusCode.Forbidden => StepUpOutcome.Rejected,
                _ => StepUpOutcome.Other,
            };
        }
        catch (HttpRequestException)
        {
            return StepUpOutcome.Network;
        }
        catch (TaskCanceledException)
        {
            return StepUpOutcome.Network;
        }
    }

    /// <summary>True for the server's "confirm it's you first" refusal.</summary>
    public static async Task<bool> IsStepUpRequiredAsync(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Forbidden && await ProblemTypeAsync(response) == StepUpRequiredType;

    /// <summary>The RFC 9457 problem type of a refused response, or null. Buffers the body, so the
    /// caller can still read it afterwards.</summary>
    public static async Task<string?> ProblemTypeAsync(HttpResponseMessage response)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out var type)
                ? type.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
