using System.Text;
using Whisper.net;

namespace StudyLife.Stt;

/// <summary>
/// Wraps a single loaded Whisper model for local, self-hosted speech-to-text. Unlike Piper's
/// per-language voices, one multilingual Whisper model covers all of StudyLife's languages -
/// no per-language registry needed here. Loaded once and reused across requests: model load
/// reads the GGML weights into memory, a real cost that transcription itself doesn't repeat.
/// </summary>
public sealed class WhisperTranscriber : IDisposable
{
    private readonly WhisperFactory _factory;

    public WhisperTranscriber(string modelPath)
    {
        _factory = WhisperFactory.FromPath(modelPath);
    }

    /// <param name="wavStream">16 kHz mono PCM WAV audio.</param>
    /// <param name="language">
    /// 2-letter language code to bias decoding toward - same convention as TtsController's
    /// "lang" query parameter. Null/empty auto-detects the language from the first samples
    /// instead, at the cost of an extra detection pass.
    /// </param>
    public async Task<string> TranscribeAsync(Stream wavStream, string? language, CancellationToken cancellationToken = default)
    {
        var builder = _factory.CreateBuilder();
        builder = string.IsNullOrWhiteSpace(language) ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        using var processor = builder.Build();

        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(wavStream, cancellationToken))
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(segment.Text.Trim());
        }
        return text.ToString();
    }

    public void Dispose() => _factory.Dispose();
}
