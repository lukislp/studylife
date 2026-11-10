// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Client.Services;

/// <summary>
/// The one question the AI features ask before they send anything: "may I continue?". The rule
/// (is consent required, does the stored one cover the current provider) lives on the server in
/// AiConsentService and arrives here as <see cref="AiConsentStatusDto"/>; this class only turns
/// it into the dialog flow. Callers (AgentChatModal, SetupAiIntegrationCard, MainLayout) call
/// <see cref="EnsureConsentAsync"/> and continue on true; AiConsentModal, the single dialog,
/// renders <see cref="Pending"/> and answers it through <see cref="AgreeAsync"/> /
/// <see cref="DeclineAsync"/>.
///
/// Held by AppStateService (<c>State.AiConsent</c>) instead of being a DI registration of its own:
/// the native apps compile this client into their bundle but build their own service container
/// (see studylife-app's native-parity check), so a new registration here would have to be
/// mirrored there before the next app release, while AppStateService is already registered on
/// both sides.
/// </summary>
public sealed class AiConsentState
{
    /// <summary>Problem code (RFC 9457 <c>type</c>) of the server's 403 while consent is missing.</summary>
    public const string ConsentRequiredProblem = "ai-consent-required";

    // A grant that races a provider change is answered with 409 and the new description; the
    // dialog is then shown again for it. Bounded so a provider flapping every request cannot
    // keep the user in a loop forever.
    private const int MaxProviderChangeRetries = 3;

    private readonly HttpClient _http;
    private AiConsentStatusDto? _status;

    public AiConsentState(HttpClient http) => _http = http;

    /// <summary>The question currently on screen, or null. Changes raise <see cref="PendingChanged"/>.</summary>
    public AiConsentRequest? Pending { get; private set; }

    public event Action? PendingChanged;

    /// <summary>The server's view of the consent. Cached for the page's lifetime; every decision
    /// point asks with <paramref name="refresh"/> so it never acts on a stale answer. Null when the
    /// server cannot be reached.</summary>
    public async Task<AiConsentStatusDto?> GetStatusAsync(bool refresh = false)
    {
        if (_status is not null && !refresh) return _status;
        try
        {
            var response = await _http.GetAsync("api/ai/consent");
            if (!response.IsSuccessStatusCode) return _status;
            _status = await response.Content.ReadFromJsonAsync<AiConsentStatusDto>();
        }
        catch
        {
            // Offline / transient: keep whatever was known before.
        }
        return _status;
    }

    /// <summary>
    /// Settles consent before something is sent: true = go ahead (no add-on, nothing required, a
    /// stored consent covers the current provider, or the user just agreed); false = do not
    /// continue (the user chose "not now", or the server could not be asked). Declining leaves AI
    /// as it was and is not an error.
    /// </summary>
    public async Task<bool> EnsureConsentAsync()
    {
        for (var attempt = 0; attempt <= MaxProviderChangeRetries; attempt++)
        {
            var status = await GetStatusAsync(refresh: true);
            if (status is null) return false;
            if (!status.AiAvailable || !status.ConsentRequired || status.Granted) return true;

            switch (await AskAsync(status, existingUser: false, providerChanged: attempt > 0))
            {
                case AiConsentAnswer.Agreed: return true;
                case AiConsentAnswer.ProviderChanged: continue;
                default: return false;
            }
        }
        return false;
    }

    /// <summary>
    /// App start: an existing AI user (has a key) without a covering consent - including one whose
    /// provider changed since they agreed - is asked once. Agreeing keeps AI on; "not now"
    /// switches it off and revokes the key (which purges their data in studylife-ai).
    /// Returns whether AI is available at all, for the caller's own UI.
    /// </summary>
    public async Task<bool> CheckAtStartupAsync()
    {
        var status = await GetStatusAsync(refresh: true);
        if (status is null) return false;
        if (!status.NeedsDecision) return status.AiAvailable;

        for (var attempt = 0; attempt <= MaxProviderChangeRetries; attempt++)
        {
            var answer = await AskAsync(status, existingUser: true, providerChanged: attempt > 0);
            if (answer == AiConsentAnswer.ProviderChanged)
            {
                status = await GetStatusAsync(refresh: true) ?? status;
                continue;
            }
            break;
        }
        return (await GetStatusAsync(refresh: true))?.AiAvailable ?? false;
    }

    /// <summary>Withdraws the consent and revokes the AI key (server side, which purges the data).</summary>
    public async Task<bool> WithdrawAsync()
    {
        try
        {
            var response = await _http.PostAsync("api/ai/consent/withdraw", null);
            if (!response.IsSuccessStatusCode) return false;
        }
        catch
        {
            return false;
        }
        await GetStatusAsync(refresh: true);
        return true;
    }

    /// <summary>True when a response is the server's 403 "ai-consent-required" problem - the
    /// proxy and key endpoints answer this while no consent covers the current provider.</summary>
    public static async Task<bool> IsConsentRequiredAsync(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden) return false;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("type", out var type)
                && type.GetString() == ConsentRequiredProblem;
        }
        catch
        {
            return false;
        }
    }

    // ── the dialog's side ────────────────────────────────────────────────────────────────────

    /// <summary>The dialog's "agree": records the consent for the description it showed. False when
    /// that failed (the dialog stays open and says so); a provider change since the dialog opened
    /// swaps in the new description and keeps it open for another look.</summary>
    public async Task<bool> AgreeAsync()
    {
        var request = Pending;
        if (request is null) return false;
        try
        {
            var response = await _http.PostAsJsonAsync("api/ai/consent/grant",
                new AiConsentGrantRequestDto { Fingerprint = request.Status.Fingerprint });
            if (response.IsSuccessStatusCode)
            {
                _status = await response.Content.ReadFromJsonAsync<AiConsentStatusDto>();
                Complete(request, AiConsentAnswer.Agreed);
                return true;
            }
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                // The provider changed after the dialog opened: nothing was recorded.
                Complete(request, AiConsentAnswer.ProviderChanged);
                return true;
            }
        }
        catch
        {
            // Falls through: the dialog shows the generic error and stays open.
        }
        return false;
    }

    /// <summary>The dialog's "not now". For an existing AI user this also withdraws (revokes the
    /// key); for everyone else nothing changes.</summary>
    public async Task<bool> DeclineAsync()
    {
        var request = Pending;
        if (request is null) return false;
        if (request.ExistingUser && !await WithdrawAsync()) return false;
        Complete(request, AiConsentAnswer.Declined);
        return true;
    }

    private async Task<AiConsentAnswer> AskAsync(AiConsentStatusDto status, bool existingUser, bool providerChanged)
    {
        // One dialog at a time: a second caller waits for the first answer instead of stacking.
        while (Pending is { } open) await open.Answer.Task;
        var request = new AiConsentRequest(status, existingUser, providerChanged);
        Pending = request;
        PendingChanged?.Invoke();
        return await request.Answer.Task;
    }

    private void Complete(AiConsentRequest request, AiConsentAnswer answer)
    {
        if (ReferenceEquals(Pending, request)) Pending = null;
        PendingChanged?.Invoke();
        request.Answer.TrySetResult(answer);
    }
}

/// <summary>What the dialog shows and waits for.</summary>
public sealed class AiConsentRequest
{
    public AiConsentRequest(AiConsentStatusDto status, bool existingUser, bool providerChanged)
    {
        Status = status;
        ExistingUser = existingUser;
        ProviderChanged = providerChanged;
    }

    public AiConsentStatusDto Status { get; }
    /// <summary>The user already has AI switched on (app-start prompt): declining switches it off.</summary>
    public bool ExistingUser { get; }
    /// <summary>The provider changed while an earlier dialog was open: say so.</summary>
    public bool ProviderChanged { get; }
    internal TaskCompletionSource<AiConsentAnswer> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public enum AiConsentAnswer
{
    Agreed,
    Declined,
    ProviderChanged,
}
