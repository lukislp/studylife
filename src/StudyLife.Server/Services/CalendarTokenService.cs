using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

/// <summary>How a calendar-token operation ended - its own small enum rather than
/// <see cref="ServiceOutcome"/>, because <see cref="DemoNotSeeded"/> maps onto a 503 with a
/// body no other endpoint produces, and inventing a shared outcome for exactly one caller would
/// make the general mapping harder to read for everyone else.</summary>
public enum CalendarTokenOutcome
{
    Ok,
    /// <summary>The session's user id no longer resolves to a row - 401.</summary>
    UnknownUser,
    /// <summary>Demo instance without a pre-seeded token; creating one would be a write - 503.</summary>
    DemoNotSeeded,
}

public readonly record struct CalendarTokenResult(CalendarTokenOutcome Outcome, string? Token);

/// <summary>
/// Per-user token for the subscribable ICS calendar feed (AuthUserEntity.CalendarToken).
/// Replaces the former global CalendarTokenProvider (a single, process-wide token for all
/// users) - which, in multi-user operation, would have shown every caller the same calendar
/// (that of the first registered user), regardless of who is actually fetching the token.
/// Both operations take the calling user's id explicitly: it comes from
/// HttpContext.SessionAuthUserId(), which only exists because the two endpoints require a REAL
/// passkey session - resolving it is <see cref="SystemController"/>'s job, acting on it is this
/// service's.
/// </summary>
public interface ICalendarTokenService
{
    /// <summary>The permanent calendar token - lazily created on first fetch, so a user who never
    /// uses the feature doesn't have a token sitting in the DB either.</summary>
    Task<CalendarTokenResult> GetOrCreateAsync(int userId);

    /// <summary>Manual "regenerate now" (e.g. on suspicion of a leak). Immediately breaks every
    /// existing calendar subscription of this user; the user must resubscribe to the ICS URL
    /// afterward.</summary>
    Task<CalendarTokenResult> RegenerateAsync(int userId);
}

public class CalendarTokenService(StudyLifeDb db, IConfiguration config) : ICalendarTokenService
{
    public async Task<CalendarTokenResult> GetOrCreateAsync(int userId)
    {
        var user = await db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return new(CalendarTokenOutcome.UnknownUser, null);

        if (user.CalendarToken is null)
        {
            // On a demo instance this branch is normally unreachable (DemoSeeder pre-seeds the
            // token), but this lazy create runs on a GET that PERSISTS - the write-block
            // middleware in Program.cs only covers non-GET methods, so without this check a
            // seeder change (e.g. dropping the pre-seeded token, or a second demo user) would
            // silently turn that endpoint into the demo's only visitor-reachable DB write.
            if (DemoModeGuard.IsEnabled(config))
                return new(CalendarTokenOutcome.DemoNotSeeded, null);
            user.CalendarToken = AuthSessionService.GenerateToken();
            user.CalendarTokenCreatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        return new(CalendarTokenOutcome.Ok, user.CalendarToken);
    }

    public async Task<CalendarTokenResult> RegenerateAsync(int userId)
    {
        var user = await db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return new(CalendarTokenOutcome.UnknownUser, null);

        user.CalendarToken = AuthSessionService.GenerateToken();
        user.CalendarTokenCreatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return new(CalendarTokenOutcome.Ok, user.CalendarToken);
    }
}
