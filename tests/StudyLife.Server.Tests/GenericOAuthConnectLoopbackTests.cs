// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// 2026-11 audit M5, dynamic clients: a loopback redirect_uri matches a registered loopback URI
/// by scheme, host, path and query - the port is free (RFC 8252 §7.3) - and a client that never
/// registered a loopback URI can not receive loopback redirects. Non-loopback URIs stay exact.
/// </summary>
public class GenericOAuthConnectLoopbackTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public GenericOAuthConnectLoopbackTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static readonly List<string> Scopes = new() { "WebhooksProxy.List" };

    private async Task SeedClientAsync(string clientId, string redirectUris) => await _factory.WithDbAsync(async db =>
    {
        db.OAuthClients.Add(new OAuthClientEntity
        {
            ClientId = clientId,
            Name = "Loopback add-on",
            Description = "d",
            AllowedRedirectUris = redirectUris,
            RequestedScopes = string.Join(',', Scopes),
            OwnerAuthUserId = 1,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    });

    private Task CleanupAsync(string clientId) => _factory.WithDbAsync(async db =>
    {
        db.OAuthClients.RemoveRange(await db.OAuthClients.Where(c => c.ClientId == clientId).ToListAsync());
        db.ClientApiKeys.RemoveRange(await db.ClientApiKeys.Where(k => k.ClientId == clientId).ToListAsync());
        await db.SaveChangesAsync();
    });

    private async Task<HttpResponseMessage> ConnectAsync(string clientId, string redirectUri)
    {
        var session = _factory.CreateClient();
        return await session.PostAsJsonAsync("/api/auth/connect", new GenericConnectRequestDto
        {
            ClientId = clientId,
            RedirectUri = redirectUri,
            State = "s",
            Scopes = Scopes,
            // Dynamic clients always need PKCE; these tests are about the redirect match only.
            CodeChallenge = TestPkce.Challenge,
            CodeChallengeMethod = "S256",
        });
    }

    [Fact]
    public async Task RegisteredLoopbackUri_MatchesOtherPortOfSamePath()
    {
        await SeedClientAsync("loop-ok", "http://127.0.0.1:8765/callback");
        var response = await ConnectAsync("loop-ok", "http://127.0.0.1:54321/callback");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<GenericConnectResponseDto>();
        Assert.StartsWith("http://127.0.0.1:54321/callback?", result!.RedirectTo);
        await CleanupAsync("loop-ok");
    }

    [Fact]
    public async Task RegisteredLoopbackUri_DifferentPath_IsRefused()
    {
        await SeedClientAsync("loop-path", "http://127.0.0.1:8765/callback");
        var response = await ConnectAsync("loop-path", "http://127.0.0.1:54321/steal");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await CleanupAsync("loop-path");
    }

    [Fact]
    public async Task ClientWithoutLoopbackRegistration_CannotReceiveLoopbackRedirects()
    {
        await SeedClientAsync("loop-none", "https://example-addon.test/callback");
        var response = await ConnectAsync("loop-none", "http://127.0.0.1:54321/callback");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await CleanupAsync("loop-none");
    }

    [Fact]
    public async Task HttpsRegistration_StaysExactMatch()
    {
        await SeedClientAsync("loop-https", "https://example-addon.test/callback");

        Assert.Equal(HttpStatusCode.OK, (await ConnectAsync("loop-https", "https://example-addon.test/callback")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ConnectAsync("loop-https", "https://example-addon.test/callback/")).StatusCode);
        await CleanupAsync("loop-https");
    }
}
