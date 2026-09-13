namespace StudyLife.Server.Configuration;

/// <summary>
/// Master switch for the speech features. False skips the TTS/STT registrations entirely
/// (defense in depth on the worker, which never calls them).
/// </summary>
public sealed class SpeechOptions
{
    public const string SectionName = "Speech";

    public bool Enabled { get; set; } = true;
}

/// <summary>"Read note aloud" (Piper). Paths default to content-root-relative directories, so
/// the options object alone cannot carry the default - see Program.cs.</summary>
public sealed class TtsOptions
{
    public const string SectionName = "Tts";

    /// <summary>ONNX voices baked into the image; null falls back to
    /// &lt;ContentRoot&gt;/tts-voices.</summary>
    public string? VoicesDirectory { get; set; }

    /// <summary>Bounded per-pod cache for synthesized audio, in megabytes.</summary>
    public int CacheSizeMb { get; set; } = 32;
}

/// <summary>Voice dictation (Whisper).</summary>
public sealed class SttOptions
{
    public const string SectionName = "Stt";

    /// <summary>ggml model file; null falls back to
    /// &lt;ContentRoot&gt;/stt-model/ggml-base.bin.</summary>
    public string? ModelPath { get; set; }
}
