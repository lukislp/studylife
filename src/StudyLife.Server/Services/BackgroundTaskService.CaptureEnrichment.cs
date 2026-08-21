using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

public partial class BackgroundTaskService
{
    // Small bound per tick (like a queue depth cap, not a real pagination concern) - a capture
    // rate high enough to matter here would be unusual for a personal single-user tool, and any
    // backlog just gets picked up on the next 5s tick regardless.
    private const int MaxCaptureEnrichmentsPerTick = 5;

    // Bounded retry, not single-shot or indefinite (found live 2026-08-21: the very first
    // attempt for a real capture failed on a transient "connection refused" - a NetworkPolicy
    // gap that made studylife-ai unreachable from this worker deployment specifically - and,
    // under the previous single-attempt design, was never retried once the underlying issue was
    // fixed minutes later). 3 attempts is enough to ride out a typical brief outage (a pod
    // restart, a short network blip) without retrying forever against a genuinely broken
    // integration.
    private const int MaxEnrichmentAttempts = 3;

    // Minimum time between attempts for the SAME note - at the normal 5s tick cadence, 3
    // attempts with no backoff would burn through in 15 seconds, far too fast to outlast a real
    // outage window (a deployment rollout typically takes at least a minute). 60s between
    // attempts gives a transient failure a realistic chance to resolve itself before the next
    // try, while 3 attempts total still bounds the worst case to a few minutes, not forever.
    private static readonly TimeSpan MinRetryBackoff = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs on EVERY tick unconditionally (like RunPushNotificationsAsync/RunLiveActivityPushAsync),
    /// not behind an hourly gate - a capture should get enriched promptly, not up to an hour
    /// late. The "power switch" gate is AiProxyClient.Enabled (mirrors ApnsSender/RunLiveActivityPushAsync's
    /// own gate) - without StudyLifeAi:BaseUrl/SharedSecret configured, this is a silent no-op,
    /// notes stay unenriched (SourceUrl set, EnrichedAt null) until the integration is
    /// configured, at which point the next tick picks them up retroactively.
    ///
    /// EnrichedAt is set once a note has either succeeded or exhausted MaxEnrichmentAttempts -
    /// a note that failed but still has attempts left is left EnrichedAt == null (still
    /// "pending" from the query's point of view) so a later tick retries it, gated by
    /// MinRetryBackoff via LastEnrichmentAttemptAt so retries don't hammer every 5s.
    /// </summary>
    internal async Task RunCaptureEnrichmentAsync(StudyLifeDb db)
    {
        if (_aiProxyClient is not { Enabled: true }) return;

        var backoffCutoff = LocalNow - MinRetryBackoff;
        var pending = await db.Notes
            .Where(n => n.SourceUrl != null && n.EnrichedAt == null
                     && (n.LastEnrichmentAttemptAt == null || n.LastEnrichmentAttemptAt <= backoffCutoff))
            .OrderBy(n => n.CreatedAt)
            .Take(MaxCaptureEnrichmentsPerTick)
            .ToListAsync();
        if (pending.Count == 0) return;

        // Every note in `pending` belongs to the SAME user (the outer per-user tick loop already
        // scopes `db`'s query filters, see CurrentUserAccessor) - one shared settings fetch per
        // tick, not one per note.
        var activeCourseIds = await GetActiveCourseIdsAsync(db);

        foreach (var note in pending)
        {
            var result = await _aiProxyClient.EnrichCaptureAsync(
                _currentAuthUserId, note.Id, note.Title, note.Content, note.SourceUrl,
                activeCourseIds, CancellationToken.None);

            note.EnrichmentAttempts++;
            note.LastEnrichmentAttemptAt = LocalNow;

            if (result != null)
            {
                note.EnrichedAt = LocalNow;
                // Only fills in a course if the note doesn't already have one - captures always
                // start with CourseId null (see studylife-capture's api.ts), but a user editing
                // the note in the few seconds before this tick runs must win, not get overwritten.
                if (result.CourseId.HasValue && note.CourseId is null)
                    note.CourseId = result.CourseId;
                if (result.Tags.Count > 0)
                    note.Tags = string.Join(", ", result.Tags);
                note.Summary = result.Summary;
                if (result.RelatedNoteIds.Count > 0)
                    note.RelatedNoteIds = string.Join(",", result.RelatedNoteIds);
            }
            else if (note.EnrichmentAttempts >= MaxEnrichmentAttempts)
            {
                // Give up permanently - same reasoning as the old single-attempt design, just
                // deferred until the retry budget is actually exhausted instead of after one try.
                note.EnrichedAt = LocalNow;
            }
            // else: leave EnrichedAt null - still "pending", picked up again once
            // MinRetryBackoff has passed.
        }

        await db.SaveChangesAsync();
    }

    private static async Task<List<int>> GetActiveCourseIdsAsync(StudyLifeDb db)
    {
        var selectedCourseIds = await db.Settings
            .Select(s => s.SelectedCourseIds)
            .FirstOrDefaultAsync();
        return string.IsNullOrEmpty(selectedCourseIds)
            ? new List<int>()
            : selectedCourseIds.Split(',').Select(int.Parse).ToList();
    }
}
