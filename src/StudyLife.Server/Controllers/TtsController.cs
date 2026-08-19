using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    private readonly StudyLifeDb _db;
    private readonly PiperVoiceRegistry _voices;
    private readonly EspeakPhonemizer _phonemizer;

    public TtsController(StudyLifeDb db, PiperVoiceRegistry voices, EspeakPhonemizer phonemizer)
    {
        _db = db;
        _voices = voices;
        _phonemizer = phonemizer;
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

        var phonemes = _phonemizer.Phonemize(text, voice.EspeakVoice);
        var wav = voice.SynthesizeWav(phonemes);
        return File(wav, "audio/wav");
    }
}
