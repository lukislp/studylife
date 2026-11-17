// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>How a calendar-token operation ended.</summary>
public enum CalendarTokenOutcome
{
    Ok,
    /// <summary>The session's user id no longer resolves to a row - 401.</summary>
    UnknownUser,
}

/// <summary>Result of <see cref="ICalendarTokenService.RegenerateAsync"/>: the new plaintext token,
/// the only moment it exists.</summary>
public readonly record struct CalendarTokenResult(CalendarTokenOutcome Outcome, string? Token);

/// <summary>Result of <see cref="ICalendarTokenService.GetStatusAsync"/>.</summary>
public readonly record struct CalendarLinkStatusResult(CalendarTokenOutcome Outcome, CalendarTokenResponseDto? Status);

/// <summary>
/// Per-user token for the subscribable ICS calendar feed (AuthUserEntity.CalendarTokenHash).
/// Replaces the former global CalendarTokenProvider (a single, process-wide token for all
/// users) - which, in multi-user operation, would have shown every caller the same calendar
/// (that of the first registered user), regardless of who is actually fetching the token.
/// Stored only as a hash (audit 2026-11-16 L5), so the plaintext is handed out exactly once, by
/// <see cref="RegenerateAsync"/>; the setup page tells the user to copy it then.
/// Both operations take the calling user's id explicitly: it comes from
/// HttpContext.SessionAuthUserId(), which only exists because the two endpoints require a REAL
/// passkey session - resolving it is <see cref="SystemController"/>'s job, acting on it is this
/// service's.
/// </summary>
public interface ICalendarTokenService
{
    /// <summary>Whether a calendar link exists and since when - read-only, never the token.</summary>
    Task<CalendarLinkStatusResult> GetStatusAsync(int userId);

    /// <summary>Creates the calendar link, or replaces it (e.g. on suspicion of a leak). Replacing
    /// immediately breaks every existing calendar subscription of this user; the user must
    /// resubscribe to the ICS URL afterward.</summary>
    Task<CalendarTokenResult> RegenerateAsync(int userId);
}

public class CalendarTokenService(StudyLifeDb db) : ICalendarTokenService
{
    public async Task<CalendarLinkStatusResult> GetStatusAsync(int userId)
    {
        var user = await db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return new(CalendarTokenOutcome.UnknownUser, null);
        return new(CalendarTokenOutcome.Ok, ToStatus(user));
    }

    public async Task<CalendarTokenResult> RegenerateAsync(int userId)
    {
        var user = await db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return new(CalendarTokenOutcome.UnknownUser, null);

        var token = AuthSessionService.GenerateToken();
        user.CalendarTokenHash = AuthSessionService.HashToken(token);
        user.CalendarTokenCreatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return new(CalendarTokenOutcome.Ok, token);
    }

    /// <summary>The status as GET api/system/calendar-token and the setup bundle report it.</summary>
    internal static CalendarTokenResponseDto ToStatus(AuthUserEntity user) => new()
    {
        Exists = user.CalendarTokenHash is not null,
        CreatedAt = user.CalendarTokenHash is null ? null : user.CalendarTokenCreatedAt,
    };
}
