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

        // Chunked, not one phonemize+inference call over the whole note: a single ONNX
        // inference call's memory cost grows much faster than linearly with sequence length,
        // which OOMKilled every production pod on a long, table-heavy note even at a limit
        // that comfortably handled short ones. TextChunker bounds each call's input length
        // regardless of how long the overall note is.
        var phonemeChunks = TextChunker.Chunk(text).Select(chunk => _phonemizer.Phonemize(chunk, voice.EspeakVoice));
        var wav = voice.SynthesizeWav(phonemeChunks);
        await _cache.SetAsync(cacheKey, wav, new DistributedCacheEntryOptions().SetAbsoluteExpiration(CacheTtl));
        return File(wav, "audio/wav");
    }

    private static string CacheKey(string lang, string text) =>
        $"tts:{lang}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))}";
}
