namespace StudyLife.Tts;

/// <summary>
/// Lazily loads and caches one <see cref="PiperVoice"/> per language code from a directory
/// containing "{lang}.onnx" + "{lang}.onnx.json" pairs. Loading a voice does real work (ONNX
/// Runtime session init), so each one is loaded once on first use and reused across requests,
/// not per-call. Which languages are actually present is a deployment concern (see the
/// server's Dockerfile), not something this class decides.
/// </summary>
public sealed class PiperVoiceRegistry(string voicesDirectory) : IDisposable
{
    private readonly Dictionary<string, PiperVoice> _loaded = new();
    private readonly Lock _lock = new();

    public PiperVoice? TryGet(string lang)
    {
        lock (_lock)
        {
            if (_loaded.TryGetValue(lang, out var cached)) return cached;

            var modelPath = Path.Combine(voicesDirectory, $"{lang}.onnx");
            var configPath = Path.Combine(voicesDirectory, $"{lang}.onnx.json");
            if (!File.Exists(modelPath) || !File.Exists(configPath)) return null;

            var voice = new PiperVoice(modelPath, configPath);
            _loaded[lang] = voice;
            return voice;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var voice in _loaded.Values) voice.Dispose();
            _loaded.Clear();
        }
    }
}
