// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// 2026-11-16 audit, M8: the setup code (register/begin and password/register while unclaimed) and
/// the device-link code (register/begin-linked) are short secrets whose only IP-independent cap
/// used to be none - with a forged X-Forwarded-For per request every guess got a fresh per-IP
/// bucket. Each path now also has one instance-wide bucket (60 per 15 minutes). Every request below
/// claims a different client IP, as a trusted-but-abused proxy would; the 61st is still refused.
/// Own factory per test: the bucket is per process.
/// </summary>
public class RegistrationGlobalLimitTests
{
    private static async Task<List<HttpStatusCode>> FireAsync(string path, object body)
    {
        await using var factory = new CustomWebApplicationFactory();
        using var client = ApiKeyTestHelpers.CreateClientWithKey(factory, null);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 61; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            statuses.Add((await client.SendAsync(request)).StatusCode);
        }
        return statuses;
    }

    [Fact]
    public async Task SetupCodeGuessing_HitsTheInstanceWideBucket_WhateverTheClientIp()
    {
        var statuses = await FireAsync("/api/auth/register/begin",
            new PasskeyRegisterBeginRequestDto { DisplayName = "Guess", SetupSecret = "WRONG-CODE" });

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(60));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[60]);
    }

    [Fact]
    public async Task LinkCodeGuessing_HitsTheInstanceWideBucket_WhateverTheClientIp()
    {
        var statuses = await FireAsync("/api/auth/register/begin-linked", new DeviceLinkRedeemRequestDto { Code = "AAAA-AAAA" });

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(60));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[60]);
    }
}
