using Microsoft.AspNetCore.Mvc;
using StudyLife.Shared;
using StudyLife.Stt;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Voice dictation ("speak a note instead of typing it") - the reverse of TtsController.
/// Deliberately note-independent: transcription doesn't read or write any note, it just turns
/// uploaded audio into text for the client to insert wherever it likes, so there's no note
/// ownership to check beyond the API gate's normal session/API-key requirement (Program.cs).
/// </summary>
[ApiController]
[Route("api/dictate")]
public class DictationController : ControllerBase
{
    // Null when Speech:Enabled=false (Program.cs) - only the k8s worker Deployment sets that
    // (audit finding O6: the worker never serves user traffic, so it never legitimately reaches
    // this controller). Same optional-service pattern as BackupController's
    // DatabaseBackupService?/DatabaseRestoreService? for the Postgres case.
    private readonly WhisperTranscriber? _transcriber;

    public DictationController(WhisperTranscriber? transcriber = null)
    {
        _transcriber = transcriber;
    }

    /// <param name="audio">16 kHz mono PCM WAV audio.</param>
    /// <param name="lang">
    /// 2-letter language code to bias decoding toward (same convention as TtsController's "lang")
    /// - omitted auto-detects the language instead, at the cost of an extra detection pass.
    /// </param>
    [HttpPost]
    [RequestSizeLimit(50L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 50L * 1024 * 1024)]
    public async Task<ActionResult<DictationResponseDto>> Transcribe(IFormFile? audio, [FromQuery] string? lang)
    {
        // Covers both "Speech:Enabled=false, never registered" and "registered but the model
        // file isn't baked into this image" with the same response - a caller has no legitimate
        // way to tell those apart, and shouldn't need to.
        if (_transcriber is null || !_transcriber.IsModelAvailable)
            return NotFound(new { error = "no speech-to-text model available on this server" });
        if (audio == null || audio.Length == 0)
            return BadRequest(new { error = "audio file is required" });

        await using var stream = audio.OpenReadStream();
        var text = await _transcriber.TranscribeAsync(stream, lang, HttpContext.RequestAborted);
        return Ok(new DictationResponseDto { Text = text });
    }
}
