// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

public class SessionInvalidationPolicyTests
{
    private static readonly Uri BaseAddress = new("https://studylife.test/");
    private static readonly Uri ApiUri = new("https://studylife.test/api/settings");

    [Theory]
    [InlineData("token-a", "token-a")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void A_401_for_the_session_that_is_still_current_ends_it(string? sent, string? current) =>
        Assert.True(SessionInvalidationPolicy.EndsSession(ApiUri, BaseAddress, HttpStatusCode.Unauthorized, sent, current));

    [Theory]
    [InlineData(null, "fresh-token")]
    [InlineData("old-token", "new-token")]
    [InlineData("old-token", null)]
    public void A_401_for_a_session_that_is_no_longer_current_is_stale(string? sent, string? current) =>
        Assert.False(SessionInvalidationPolicy.EndsSession(ApiUri, BaseAddress, HttpStatusCode.Unauthorized, sent, current));

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void Other_statuses_never_end_the_session(HttpStatusCode status) =>
        Assert.False(SessionInvalidationPolicy.EndsSession(ApiUri, BaseAddress, status, "t", "t"));

    [Fact]
    public void Auth_paths_and_foreign_hosts_never_end_the_session()
    {
        Assert.False(SessionInvalidationPolicy.EndsSession(
            new Uri("https://studylife.test/api/auth/login/finish"), BaseAddress, HttpStatusCode.Unauthorized, "t", "t"));
        Assert.False(SessionInvalidationPolicy.EndsSession(
            new Uri("https://elsewhere.test/api/settings"), BaseAddress, HttpStatusCode.Unauthorized, "t", "t"));
        Assert.False(SessionInvalidationPolicy.EndsSession(null, BaseAddress, HttpStatusCode.Unauthorized, "t", "t"));
    }
}
