// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// 2026-11-16 audit, finding M1: a session token alone must not be enough for lasting access.
/// Approving or removing a passkey, generating recovery codes, signing out the other devices and the
/// raw database download / restore need a session that confirmed its user moments ago
/// (POST api/auth/account/reauth, [RequireStepUp]); a session that signed in moments ago counts.
/// Every "stolen token" below is an aged session: one that signed in an hour ago.
/// </summary>
public class StepUpEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly AccountHttp _http;

    public StepUpEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _http = new AccountHttp(factory);
    }

    [Theory]
    [InlineData("POST", "/api/auth/recovery/generate")]
    [InlineData("POST", "/api/auth/sessions/revoke-others")]
    public async Task AStolenAgedToken_CannotRunAGuardedAction(string method, string url)
    {
        var (token, _, _) = await _http.NewUserAsync($"Aged {url}");
        await _http.AgeAsync(token);

        var response = await _http.SendAsync(new HttpMethod(method), url, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("step-up-required", await PasswordHttp.ProblemTypeAsync(response));
    }

    [Fact]
    public async Task AStolenAgedToken_CannotApproveOrRemoveAPasskey()
    {
        var (token, _, userId) = await _http.NewUserAsync("Fay");
        var pendingId = await AddPendingPasskeyAsync(userId, createdBySessionId: null);
        await _http.AgeAsync(token);

        var approve = await _http.SendAsync(HttpMethod.Post, $"/api/auth/credentials/{pendingId}/approve", token);
        var remove = await _http.SendAsync(HttpMethod.Delete, $"/api/auth/credentials/{pendingId}", token);

        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        await _factory.WithDbAsync(async db =>
        {
            var credential = await db.PasskeyCredentials.SingleAsync(c => c.Id == pendingId);
            Assert.Null(credential.ApprovedAt);
        });
    }

    [Fact]
    public async Task APasskeyProof_ConfirmsTheSession_AndUnlocksTheGuardedActions()
    {
        var (token, passkey, _) = await _http.NewUserAsync("Gus");
        await _http.AgeAsync(token);

        var confirm = await _http.SendAsync(HttpMethod.Post, "/api/auth/account/reauth", token, await _http.PasskeyProofAsync(token, passkey));
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);

        var generate = await _http.SendAsync(HttpMethod.Post, "/api/auth/recovery/generate", token);
        Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
        Assert.Equal(8, (await generate.Content.ReadFromJsonAsync<RecoveryCodesResponseDto>())!.Codes.Count);
        var revoke = await _http.SendAsync(HttpMethod.Post, "/api/auth/sessions/revoke-others", token);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
    }

    [Fact]
    public async Task TheConfirmation_BelongsToOneSession_NotToTheAccount()
    {
        var (token, passkey, _) = await _http.NewUserAsync("Hal");
        var other = await _http.SiblingSessionAsync(token);
        await _http.AgeAsync(token);
        await _http.AgeAsync(other);

        var confirm = await _http.SendAsync(HttpMethod.Post, "/api/auth/account/reauth", token, await _http.PasskeyProofAsync(token, passkey));
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);

        var fromOther = await _http.SendAsync(HttpMethod.Post, "/api/auth/recovery/generate", other);
        Assert.Equal(HttpStatusCode.Forbidden, fromOther.StatusCode);
    }

    [Fact]
    public async Task Reauth_WithoutProof_OrWithAWrongOne_IsRefused()
    {
        var (token, _, _) = await _http.NewUserAsync("Ivy");
        await _http.AgeAsync(token);

        var missing = await _http.SendAsync(HttpMethod.Post, "/api/auth/account/reauth", token, new JsonObject());
        var wrong = await _http.SendAsync(HttpMethod.Post, "/api/auth/account/reauth", token, new JsonObject { ["recoveryCode"] = "AAAA-BBBB-CCCC" });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("reauthentication-required", await PasswordHttp.ProblemTypeAsync(missing));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal("reauthentication-failed", await PasswordHttp.ProblemTypeAsync(wrong));
        var stillRefused = await _http.SendAsync(HttpMethod.Post, "/api/auth/recovery/generate", token);
        Assert.Equal(HttpStatusCode.Forbidden, stillRefused.StatusCode);
    }

    [Fact]
    public async Task RecoveryCodesTheSessionGeneratedItself_CannotConfirmIt()
    {
        var (token, _, _) = await _http.NewUserAsync("Jo");
        var ownCodes = await _http.GeneratedCodesAsync(token); // fresh session: allowed without a proof
        await _http.AgeAsync(token);

        var confirm = await _http.SendAsync(HttpMethod.Post, "/api/auth/account/reauth", token, new JsonObject { ["recoveryCode"] = ownCodes[0] });

        Assert.Equal(HttpStatusCode.Forbidden, confirm.StatusCode);
        // From another session the same code is a valid proof (the lost-passkey case).
        var other = await _http.SiblingSessionAsync(token);
        await _http.AgeAsync(other);
        var fromOther = await _http.SendAsync(HttpMethod.Post, "/api/auth/account/reauth", other, new JsonObject { ["recoveryCode"] = ownCodes[0] });
        Assert.Equal(HttpStatusCode.NoContent, fromOther.StatusCode);
    }

    [Fact]
    public async Task ASessionThatJustSignedIn_CountsAsConfirmed()
    {
        var (token, _, _) = await _http.NewUserAsync("Kai");

        var generate = await _http.SendAsync(HttpMethod.Post, "/api/auth/recovery/generate", token);

        Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
    }

    [Fact]
    public async Task TheSessionThatRegisteredAPasskey_CannotApproveIt_EvenWhenConfirmed()
    {
        var (token, passkey, userId) = await _http.NewUserAsync("Lu");
        var sessionId = await SessionIdAsync(token);
        var pendingId = await AddPendingPasskeyAsync(userId, createdBySessionId: sessionId);
        await _http.AgeAsync(token);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _http.SendAsync(HttpMethod.Post, "/api/auth/account/reauth", token, await _http.PasskeyProofAsync(token, passkey))).StatusCode);

        var selfApprove = await _http.SendAsync(HttpMethod.Post, $"/api/auth/credentials/{pendingId}/approve", token);
        Assert.Equal(HttpStatusCode.Forbidden, selfApprove.StatusCode);
        Assert.Equal("approval-needs-other-session", await PasswordHttp.ProblemTypeAsync(selfApprove));

        var otherDevice = await _http.SiblingSessionAsync(token); // signed in moments ago
        var approve = await _http.SendAsync(HttpMethod.Post, $"/api/auth/credentials/{pendingId}/approve", otherDevice);
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
    }

    [Fact]
    public async Task AStolenAgedOwnerToken_CannotDownloadOrRestoreTheRawDatabase()
    {
        var (ownerToken, ownerPasskey) = await _http.OwnerAsync();
        await _http.AgeAsync(ownerToken);

        var download = await _http.SendAsync(HttpMethod.Get, "/api/backup/database", ownerToken);
        var encrypted = await _http.SendAsync(HttpMethod.Post, "/api/backup/database/encrypted", ownerToken, new JsonObject { ["password"] = "pw" });
        using var restoreRequest = new HttpRequestMessage(HttpMethod.Post, "/api/backup/restore")
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "x.db" } },
        };
        restoreRequest.Headers.Add("X-Session-Token", ownerToken);
        var restore = await _http.Client.SendAsync(restoreRequest);

        Assert.Equal(HttpStatusCode.Forbidden, download.StatusCode);
        Assert.Equal("step-up-required", await PasswordHttp.ProblemTypeAsync(download));
        Assert.Equal(HttpStatusCode.Forbidden, encrypted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, restore.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _http.SendAsync(HttpMethod.Post, "/api/auth/account/reauth", ownerToken, await _http.PasskeyProofAsync(ownerToken, ownerPasskey))).StatusCode);
        var confirmedDownload = await _http.SendAsync(HttpMethod.Get, "/api/backup/database", ownerToken);
        Assert.Equal(HttpStatusCode.OK, confirmedDownload.StatusCode);
    }

    [Fact]
    public async Task AStolenAgedToken_CannotDownloadThePersonalDataExport()
    {
        var (token, passkey, _) = await _http.NewUserAsync("Export Eve");
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(HttpMethod.Get, "/api/backup/export", token)).StatusCode);
        await _http.AgeAsync(token);

        var refused = await _http.SendAsync(HttpMethod.Get, "/api/backup/export", token);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("step-up-required", await PasswordHttp.ProblemTypeAsync(refused));
        Assert.Equal(HttpStatusCode.NoContent,
            (await _http.SendAsync(HttpMethod.Post, "/api/auth/account/reauth", token, await _http.PasskeyProofAsync(token, passkey))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(HttpMethod.Get, "/api/backup/export", token)).StatusCode);
    }

    private Task<int> SessionIdAsync(string token) => _factory.WithDbAsync(async db =>
    {
        var hash = Services.AuthSessionService.HashToken(token);
        return await db.AuthSessions.Where(s => s.TokenHash == hash).Select(s => s.Id).SingleAsync();
    });

    private Task<int> AddPendingPasskeyAsync(int userId, int? createdBySessionId) => _factory.WithDbAsync(async db =>
    {
        var credential = new Data.PasskeyCredentialEntity
        {
            AuthUserId = userId,
            CredentialId = Guid.NewGuid().ToByteArray(),
            PublicKey = [1, 2, 3],
            CreatedAt = DateTime.UtcNow,
            CreatedBySessionId = createdBySessionId,
        };
        db.PasskeyCredentials.Add(credential);
        await db.SaveChangesAsync();
        return credential.Id;
    });
}
