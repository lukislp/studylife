using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using StudyLife.Server.Data;
using StudyLife.Tts;

namespace StudyLife.Server.Controllers;

/// <summary>
/// "Read note aloud" - separate from NotesController since it's a synthesis operation, not
/// note CRUD. Deliberately GET (not POST): reads existing data and returns generated audio,
/// nothing is persisted - which also means it isn't blocked by the demo instance's
/// mutating-request gate (Program.cs), so visitors can actually try the feature there.
/// </summary>
[ApiController]
[Route("api/notes")]
public class TtsController : ControllerBase
{
    // Content-addressed, not note-ID-addressed: the cache key is a hash of the exact text
    // that would be synthesized, so an edited note automatically gets a fresh key (no explicit
    // invalidation needed) while an unchanged note - re-opened, or "Vorlesen" clicked again -
    // is served from cache instead of re-running phonemization + ONNX inference every time.
    // 24h TTL: long enough to cover "read it again later the same day", short enough that a
    // single-container deployment's in-memory cache (the Cache:Provider=Memory default) doesn't
    // accumulate audio blobs indefinitely on Raspberry-Pi-class hardware.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    // Bumped whenever the synthesis pipeline itself changes (TextChunker granularity,
    // inter-chunk silence, phonemization, ...) - not just when the note's text changes. Without
    // this, the key is purely a function of (lang, text): in production, Redis-backed and
    // therefore untouched by any pod restart/rollout, a note tested BEFORE a pacing/quality fix
    // shipped kept serving the stale pre-fix audio for up to 24h after the fix was already live,
    // because its cache key never changed. Confirmed live as the cause of "the fix doesn't
    // seem to have landed" reports for notes that had been tested earlier.
    private const int SynthesisVersion = 3;

    // A client retry (or several concurrent readers of the same note) while the first
    // synthesis is still running would otherwise each kick off their own independent ONNX
    // pass over the same content, competing for the same limited Pi CPU instead of just
    // waiting for the one already in progress - directly counterproductive on hardware slow
    // enough to need a retry in the first place. All callers for the same cache key share one
    // underlying Task and get the same result the moment it's ready, instead of each starting
    // their own.
    private static readonly ConcurrentDictionary<string, Task<byte[]>> InFlightSyntheses = new();

    private readonly StudyLifeDb _db;
    private readonly PiperVoiceRegistry _voices;
    private readonly EspeakPhonemizer _phonemizer;
    private readonly IDistributedCache _cache;

    public TtsController(StudyLifeDb db, PiperVoiceRegistry voices, EspeakPhonemizer phonemizer, IDistributedCache cache)
    {
        _db = db;
        _voices = voices;
        _phonemizer = phonemizer;
        _cache = cache;
    }

    [HttpGet("{id}/tts")]
    public async Task<IActionResult> Synthesize(int id, [FromQuery] string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return BadRequest(new { error = "lang query parameter is required" });

        var note = await _db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id);
        if (note == null) return NotFound();

        var voice = _voices.TryGet(lang);
        if (voice == null)
            return NotFound(new { error = $"no TTS voice available for language '{lang}'" });

        var text = MarkdownToSpeechText.Extract(note.Content, note.IsMarkdown);
        if (string.IsNullOrWhiteSpace(text))
            return NotFound(new { error = "note has no readable content" });

        var cacheKey = CacheKey(lang, text);
        var cached = await _cache.GetAsync(cacheKey);
        if (cached != null) return File(cached, "audio/wav");

        var phonemizer = _phonemizer;
        var cache = _cache;
        var synthesis = InFlightSyntheses.GetOrAdd(cacheKey,
            _ => Task.Run(() => SynthesizeAndCache(cacheKey, text, voice, phonemizer, cache)));
        try
        {
            var wav = await synthesis;
            return File(wav, "audio/wav");
        }
        finally
        {
            InFlightSyntheses.TryRemove(new KeyValuePair<string, Task<byte[]>>(cacheKey, synthesis));
        }
    }

    // Static + all dependencies passed explicitly (not reading instance fields) on purpose:
    // the Task this returns is stored in a dictionary that outlives any single request/
    // controller instance, so nothing here may depend on per-request state like _db (a scoped
    // DbContext that could be disposed by the time a later, unrelated request awaits the same
    // Task) - voice/phonemizer/cache are all singleton-scoped, safe to hold onto.
    //
    // Chunked, not one phonemize+inference call over the whole note: a single ONNX inference
    // call's memory cost grows much faster than linearly with sequence length, which OOMKilled
    // every production pod on a long, table-heavy note even at a limit that comfortably
    // handled short ones. TextChunker bounds each call's input length regardless of how long
    // the overall note is, and carries the pause length (sentence vs. clause-level punctuation)
    // through to PiperVoice so it can insert the right gap between the resulting audio segments.
    private static async Task<byte[]> SynthesizeAndCache(
        string cacheKey, string text, PiperVoice voice, EspeakPhonemizer phonemizer, IDistributedCache cache)
    {
        var phonemeChunks = TextChunker.Chunk(text)
            .Select(chunk => (Phonemes: phonemizer.Phonemize(chunk.Text, voice.EspeakVoice), chunk.LongPause));
        var wav = voice.SynthesizeWav(phonemeChunks);
        await cache.SetAsync(cacheKey, wav, new DistributedCacheEntryOptions().SetAbsoluteExpiration(CacheTtl));
        return wav;
    }

    private static string CacheKey(string lang, string text) =>
        $"tts:v{SynthesisVersion}:{lang}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))}";
}
