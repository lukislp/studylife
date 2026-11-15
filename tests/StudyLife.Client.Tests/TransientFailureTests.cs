// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

public class TransientFailureTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void Gateway_and_throttling_statuses_are_retried(HttpStatusCode status) =>
        Assert.True(TransientFailure.IsTransient(new HttpRequestException("x", null, status)));

    [Fact]
    public void Network_errors_and_timeouts_are_retried()
    {
        Assert.True(TransientFailure.IsTransient(new HttpRequestException("offline")));
        Assert.True(TransientFailure.IsTransient(new TaskCanceledException()));
        Assert.True(TransientFailure.IsTransient(new OperationCanceledException()));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void Other_statuses_are_permanent(HttpStatusCode status) =>
        Assert.False(TransientFailure.IsTransient(new HttpRequestException("x", null, status)));

    [Fact]
    public void Unrelated_exceptions_are_permanent() =>
        Assert.False(TransientFailure.IsTransient(new InvalidOperationException()));
}
