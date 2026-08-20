using System.Text;
using Whisper.net;

namespace StudyLife.Stt;

/// <summary>
/// Wraps a single loaded Whisper model for local, self-hosted speech-to-text. Unlike Piper's
/// per-language voices, one multilingual Whisper model covers all of StudyLife's languages -
/// no per-language registry needed here. The model is loaded lazily on first use, not in the
/// constructor - mirrors PiperVoiceRegistry's laziness (a Dockerfile concern, not a code
/// concern): registering this as a DI singleton must not crash the whole app at startup in an
/// environment without the baked model file (local "dotnet run", tests), the same way a missing
/// Piper voice never crashes startup either.
/// </summary>
public sealed class WhisperTranscriber(string modelPath) : IDisposable
{
    private readonly Lock _lock = new();
    private WhisperFactory? _factory;

    public bool IsModelAvailable => File.Exists(modelPath);

    /// <param name="wavStream">16 kHz mono PCM WAV audio.</param>
    /// <param name="language">
    /// 2-letter language code to bias decoding toward - same convention as TtsController's
    /// "lang" query parameter. Null/empty auto-detects the language from the first samples
    /// instead, at the cost of an extra detection pass.
    /// </param>
    public async Task<string> TranscribeAsync(Stream wavStream, string? language, CancellationToken cancellationToken = default)
    {
        var factory = GetOrLoadFactory();
        var builder = factory.CreateBuilder();
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

    private WhisperFactory GetOrLoadFactory()
    {
        if (_factory != null) return _factory;
        lock (_lock)
        {
            _factory ??= WhisperFactory.FromPath(modelPath);
            return _factory;
        }
    }

    public void Dispose() => _factory?.Dispose();
}
