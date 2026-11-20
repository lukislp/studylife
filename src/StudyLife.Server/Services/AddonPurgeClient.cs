// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;
using StudyLife.Server.Services.Account;

namespace StudyLife.Server.Services;

/// <summary>
/// The account-deletion purge call to ONE add-on that keeps per-user data (studylife-mcp,
/// studylife-alexa, studylife-telegram - see <see cref="AddonPurgeTarget"/>). The contract, the same
/// for every such add-on (docs/ARCHITECTURE.md "Account deletion"):
/// <code>
/// POST {BaseUrl}/internal/users/{userId}/purge
/// X-StudyLife-Shared-Secret: {SharedSecret}
/// {"key_hashes": ["&lt;64 lowercase hex&gt;", ...], "instance_url": "https://..."}
/// </code>
/// <c>key_hashes</c> may be empty (the user holds no key for the add-on, or revoked it earlier).
/// <c>instance_url</c> is this server's public origin (the pinned <c>Fido2:Origins</c> value) and is
/// left out when none is configured. An add-on that stores the user id with its links
/// (studylife-alexa, studylife-telegram) matches on user id AND instance, so the purge also reaches
/// a link whose key is gone. An add-on version that does not know the field ignores it, and one
/// that matches only by key hash answers 204 for an empty list, so server and add-ons can be
/// deployed in either order.
/// 204 (also when nothing matched) means done; 401 wrong secret, 422 invalid request, 503 the
/// add-on has no purge secret configured. Any non-2xx keeps the outbox row for a retry.
///
/// Same flat shared-secret scheme as <see cref="WebhooksProxyClient"/>. Three states, decided by
/// the settings of <see cref="AddonPurgeTarget.SectionPath"/>:
/// <list type="bullet">
/// <item>No BaseUrl: the add-on is not part of this installation. Nothing is queued and a queued
/// row counts as delivered - there is nothing to purge.</item>
/// <item>BaseUrl and SharedSecret: the call is made.</item>
/// <item>BaseUrl without SharedSecret: the add-on IS part of the installation but the call cannot
/// be authenticated yet (for example the secret is rolled out after the URL). Purges are queued and
/// stay undelivered, so they go out once the secret is set instead of being lost.</item>
/// </list>
/// </summary>
public sealed class AddonPurgeClient
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly string? _baseUrl;
    private readonly string? _sharedSecret;
    private readonly Func<string?> _instanceUrl;

    public AddonPurgeClient(AddonPurgeTarget target, AddonPurgeOptions options, ILogger<AddonPurgeClient> logger, HttpClient? httpClient = null, Func<string?>? instanceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        Target = target;
        _logger = logger;
        _instanceUrl = instanceUrl ?? (() => null);
        _http = httpClient ?? new HttpClient { Timeout = RequestTimeout };
        _baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? null : options.BaseUrl.Trim().TrimEnd('/');
        _sharedSecret = string.IsNullOrEmpty(options.SharedSecret) ? null : options.SharedSecret;
        ClientId = string.IsNullOrWhiteSpace(options.ClientId) ? target.DefaultClientId : options.ClientId.Trim();

        // Once per process (the clients are singletons): an operator reading the startup log sees
        // which add-ons account deletion reaches.
        if (Configured && _sharedSecret is not null)
            _logger.LogInformation("Account-deletion purge for {Addon} active (BaseUrl {BaseUrl})", target.Name, _baseUrl);
        else if (Configured)
            _logger.LogWarning("Account-deletion purge for {Addon}: {Section}:SharedSecret is not set - purges are queued and retried until it is", target.Name, target.SectionPath);
        else if (_sharedSecret is not null)
            _logger.LogWarning("Account-deletion purge for {Addon}: {Section}:SharedSecret is set without a BaseUrl - ignored, nothing is purged there", target.Name, target.SectionPath);
        else
            _logger.LogInformation("Account-deletion purge for {Addon} not configured ({Section}:*) - nothing to purge there", target.Name, target.SectionPath);

        // The purge carries a shared secret and key hashes: plain HTTP is only meant for an add-on
        // that cannot serve TLS (AllowPlainHttp under RequireHttps) or a private test setup.
        if (Configured && !_baseUrl!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            _logger.LogWarning("Account-deletion purge for {Addon} goes over plain HTTP ({Section}:BaseUrl is not https://): the shared secret and the key hashes are not encrypted - give the add-on a certificate and an https:// URL (docs/SECURITY-ASSURANCE.md 8.3)", target.Name, target.SectionPath);
    }

    public AddonPurgeTarget Target { get; }

    /// <summary>The OAuth client id whose ClientApiKey rows belong to this add-on (null for the
    /// MCP slot, which is a column on the user).</summary>
    public string? ClientId { get; }

    /// <summary>The add-on is part of this installation (a BaseUrl is set): account deletion
    /// queues a purge for it.</summary>
    public bool Configured => _baseUrl is not null;

    /// <summary>True = the add-on confirmed (2xx), or it is not part of this installation. False on
    /// any other answer or error, and while the secret is missing, so the outbox retries. Never
    /// throws. Logs the user id and the status only - never the hashes.</summary>
    public async Task<bool> PurgeUserAsync(int userId, IReadOnlyList<string> keyHashes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keyHashes);
        if (!Configured) return true;
        if (_sharedSecret is null) return false; // warned at startup; the drain warns per row too
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/internal/users/{userId}/purge")
            {
                Content = JsonContent.Create(new PurgeRequest(keyHashes, _instanceUrl())),
            };
            request.Headers.Add("X-StudyLife-Shared-Secret", _sharedSecret);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("{Addon} purge of user {UserId} returned {Status} - kept for retry", Target.Name, userId, (int)response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            // Type only, not the message: a transport message can carry the request URL, and the
            // outbox only needs to know that it has to retry.
            _logger.LogWarning("{Addon} purge of user {UserId} failed ({Error}) - kept for retry", Target.Name, userId, ex.GetType().Name);
            return false;
        }
    }

    private sealed record PurgeRequest(
        [property: JsonPropertyName("key_hashes")] IReadOnlyList<string> KeyHashes,
        [property: JsonPropertyName("instance_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InstanceUrl);
}

/// <summary>The purge clients of all <see cref="AddonPurgeTarget.All"/>, one per add-on, as one
/// singleton - what the outbox and the deletion use case take instead of one parameter each.</summary>
public sealed class AddonPurgeClients(IEnumerable<AddonPurgeClient> clients)
{
    private readonly IReadOnlyList<AddonPurgeClient> _clients = clients.ToList();

    public IReadOnlyList<AddonPurgeClient> All => _clients;

    public AddonPurgeClient? For(AddonPurgeTarget target) => _clients.FirstOrDefault(c => c.Target == target);

    /// <summary>The production wiring (Program.cs): one client per add-on from its named options,
    /// each with its own HttpClient from <paramref name="createHttpClient"/> (add-on TLS trust).
    /// <paramref name="instanceUrl"/> names this server's public origin for the add-ons (null = none).</summary>
    public static AddonPurgeClients Create(
        IOptionsMonitor<AddonPurgeOptions> options, ILogger<AddonPurgeClient> logger, Func<HttpClient> createHttpClient,
        Func<string?>? instanceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(createHttpClient);
        return new AddonPurgeClients(AddonPurgeTarget.All.Select(target =>
            new AddonPurgeClient(target, options.Get(target.Name), logger, createHttpClient(), instanceUrl)));
    }
}
