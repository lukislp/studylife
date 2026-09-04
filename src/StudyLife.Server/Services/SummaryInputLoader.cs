using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Controllers;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// Shared DB-loading behind every *SummaryController (Dashboard/Stats/Wrapped/Report): each
/// assembles its Shared *SummaryInput exactly like its client page would have from its own
/// fetches, and every one of them needs the same two pieces - the session table read once and
/// sliced into the same history windows GET /api/sessions/history would have returned, and the
/// notes-changed cache-key token. Factored out here so those two pieces stay in exactly one
/// place instead of drifting apart across four copies.
/// </summary>
public sealed class SummaryInputLoader
{
    private readonly StudyLifeDb _db;

    public SummaryInputLoader(StudyLifeDb db) => _db = db;

    /// <summary>All of this user's sessions, mapped once - the shared basis every history window
    /// below (and a page's own unbounded "Sessions" input, e.g. Stats/Dashboard) slices from in
    /// memory instead of paying a separate DB round trip per window.</summary>
    public async Task<List<StudySessionDto>> LoadAllSessionsAsync()
    {
        var entities = await _db.Sessions.AsNoTracking().ToListAsync();
        return entities.Select(SessionsController.ToDto).ToList();
    }

    /// <summary>
    /// Same filter as SessionsController.GetHistory/its compiled query: sessions starting within
    /// the last <paramref name="days"/> days of <paramref name="now"/>, and - unless
    /// <paramref name="onlyCompleted"/> is false - only those that count as "studied" (timer-
    /// completed, or their scheduled end has already passed). GetHistory itself compares against
    /// DateTime.Now, not UtcNow (naive local columns, see its own "audit finding Z1" comment);
    /// <paramref name="now"/> here is expected to be the caller's own single DateTime.Now read,
    /// so every window of one request slices against the exact same instant instead of several
    /// independent clock reads.
    /// </summary>
    public static List<StudySessionDto> SliceHistory(
        List<StudySessionDto> allSessions, DateTime now, int days, bool onlyCompleted = true)
    {
        var from = now.AddDays(-days);
        return allSessions
            .Where(s => s.StartTime >= from && (!onlyCompleted || s.IsCompleted || s.EndTime <= now))
            .ToList();
    }

    /// <summary>Cheap marker for "did the note set change" - Notes has no version counter of its
    /// own (unlike Sessions/Settings), so count + the newest UpdatedAt stand in for one without a
    /// dedicated Redis counter for a single field. Read on every call, not just on a cache miss,
    /// since it is itself part of the caller's cache key.</summary>
    public async Task<string> LoadNotesTokenAsync()
    {
        var stats = await _db.Notes.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), MaxUpdatedAt = (DateTime?)g.Max(n => n.UpdatedAt) })
            .FirstOrDefaultAsync();
        return stats == null ? "0" : $"{stats.Count}:{stats.MaxUpdatedAt:O}";
    }
}
