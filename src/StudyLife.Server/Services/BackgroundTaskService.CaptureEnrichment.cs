using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

public partial class BackgroundTaskService
{
    // Small bound per tick (like a queue depth cap, not a real pagination concern) - a capture
    // rate high enough to matter here would be unusual for a personal single-user tool, and any
    // backlog just gets picked up on the next 5s tick regardless.
    private const int MaxCaptureEnrichmentsPerTick = 5;

    /// <summary>
    /// Runs on EVERY tick unconditionally (like RunPushNotificationsAsync/RunLiveActivityPushAsync),
    /// not behind an hourly gate - a capture should get enriched promptly, not up to an hour
    /// late. The "power switch" gate is AiProxyClient.Enabled (mirrors ApnsSender/RunLiveActivityPushAsync's
    /// own gate) - without StudyLifeAi:BaseUrl/SharedSecret configured, this is a silent no-op,
    /// notes stay unenriched (SourceUrl set, EnrichedAt null) until the integration is
    /// configured, at which point the next tick picks them up retroactively.
    ///
    /// Single attempt per note, not indefinite retry: EnrichedAt is set whether
    /// AiProxyClient.EnrichCaptureAsync succeeds or returns null (studylife-ai down/erroring) -
    /// same reasoning as RegisterKeyAsync/RevokeKeyAsync's own never-throws-but-never-retries
    /// contract. A permanently-failing note would otherwise get re-attempted every 5s forever.
    /// </summary>
    internal async Task RunCaptureEnrichmentAsync(StudyLifeDb db)
    {
        if (_aiProxyClient is not { Enabled: true }) return;

        var pending = await db.Notes
            .Where(n => n.SourceUrl != null && n.EnrichedAt == null)
            .OrderBy(n => n.CreatedAt)
            .Take(MaxCaptureEnrichmentsPerTick)
            .ToListAsync();
        if (pending.Count == 0) return;

        foreach (var note in pending)
        {
            var result = await _aiProxyClient.EnrichCaptureAsync(
                _currentAuthUserId, note.Id, note.Title, note.Content, note.SourceUrl, CancellationToken.None);

            note.EnrichedAt = LocalNow;
            if (result != null)
            {
                // Only fills in a course if the note doesn't already have one - captures always
                // start with CourseId null (see studylife-capture's api.ts), but a user editing
                // the note in the few seconds before this tick runs must win, not get overwritten.
                if (result.CourseId.HasValue && note.CourseId is null)
                    note.CourseId = result.CourseId;
                if (result.Tags.Count > 0)
                    note.Tags = string.Join(", ", result.Tags);
                note.Summary = result.Summary;
            }
        }

        await db.SaveChangesAsync();
    }
}
