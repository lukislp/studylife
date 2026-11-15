// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;

namespace StudyLife.Client.Services;

/// <summary>
/// Tells a failure worth retrying from one that will fail the same way every time. A retry loop
/// that treats a 400 like a timeout makes the user wait minutes for an answer that was final at the
/// first attempt (read aloud with an empty language, 2026-11-14).
/// </summary>
public static class TransientFailure
{
    /// <summary>True for a client-side timeout or cancellation, a network error without any HTTP
    /// status, and a gateway-side 408, 429, 502, 503 or 504. Every other status (400, 401, 403,
    /// 404, 422, 500, ...) is permanent for the request as sent.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        OperationCanceledException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: var status } => status is
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
        _ => false,
    };
}
