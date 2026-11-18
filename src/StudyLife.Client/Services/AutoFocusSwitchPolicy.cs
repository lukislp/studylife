// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Client.Models;

namespace StudyLife.Client.Services;

/// <summary>Outcome of one auto-focus check. <see cref="HandledSessionId"/> is the session the
/// caller must remember (navigated to, or deliberately skipped); null leaves its memory unchanged.</summary>
public readonly record struct AutoFocusDecision(bool Navigate, int? HandledSessionId);

/// <summary>
/// Decides whether the "Auto-switch to Focus" setting should move the user to /focus. It fires at
/// most once per session (a session becoming active, or an upcoming one entering the
/// "minutes before" window), never pulls the user back after they leave Focus for the same
/// session, and never interrupts a flow page (connect/consent, sign-in, registration, setup,
/// note editing). On such a page the session is marked handled, so the switch is skipped for
/// that session instead of firing later out of the blue.
/// </summary>
public static class AutoFocusSwitchPolicy
{
    public const string FocusPath = "/focus";

    private static readonly string[] ProtectedPrefixes =
    [
        "/connect", "/login", "/register", "/link", "/setup", "/notes", "/shared",
        "/authorize", "/oauth",
    ];

    public static AutoFocusDecision Decide(
        UserSettings settings,
        StudySession? active,
        StudySession? upcoming,
        DateTime now,
        string currentPath,
        int? lastHandledSessionId)
    {
        if (!settings.AutoSwitchFocus)
        {
            return default;
        }

        var session = active;
        if (session is null && upcoming is not null)
        {
            var minutesUntil = (upcoming.StartTime - now).TotalMinutes;
            if (minutesUntil >= 0 && minutesUntil <= settings.AutoSwitchMinutesBefore)
            {
                session = upcoming;
            }
        }

        if (session is null || session.Id == lastHandledSessionId)
        {
            return default;
        }

        // Already on Focus counts as handled too: leaving it later must not pull the user back.
        var navigate = !IsFocus(currentPath) && !IsProtectedFlowPath(currentPath);
        return new AutoFocusDecision(navigate, session.Id);
    }

    public static bool IsFocus(string path) => Normalize(path) == FocusPath;

    public static bool IsProtectedFlowPath(string path)
    {
        var p = Normalize(path);
        foreach (var prefix in ProtectedPrefixes)
        {
            if (p == prefix || p.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string Normalize(string path)
    {
        var end = path.IndexOfAny(['?', '#']);
        if (end >= 0) path = path[..end];
        path = path.TrimEnd('/').ToLowerInvariant();
        return path.Length == 0 ? "/" : path.StartsWith('/') ? path : "/" + path;
    }
}