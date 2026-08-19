using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace StudyLife.Tts;

/// <summary>
/// One loaded Piper ONNX voice model. Takes already-phonemized IPA text (see
/// <see cref="EspeakPhonemizer"/>) and synthesizes 16-bit PCM WAV bytes.
/// </summary>
public sealed class PiperVoice : IDisposable
{
    private readonly InferenceSession _session;
    private readonly Dictionary<string, int> _phonemeIdMap;
    private readonly float _noiseScale;
    private readonly float _lengthScale;
    private readonly float _noiseW;

    public int SampleRate { get; }

    /// <summary>The espeak-ng voice name this model was trained for (config.json's espeak.voice) - e.g. "de".</summary>
    public string EspeakVoice { get; }

    public PiperVoice(string onnxModelPath, string onnxConfigPath)
    {
        // ORT's default CPU arena allocator grows in large power-of-two jumps and never
        // shrinks - fine on a workstation, but on the ~300Mi-per-pod budget this runs under
        // (Raspberry Pi cluster) it's what actually OOMKilled every studylife-web pod in
        // production, not the model file size itself. Disabling the arena (and the memory-
        // pattern optimizer, which caches its own extra buffers) trades a bit of steady-state
        // throughput for a much smaller, more predictable footprint - the right trade for a
        // feature invoked per note-read, not a hot loop.
        var sessionOptions = new SessionOptions
        {
            EnableCpuMemArena = false,
            EnableMemoryPattern = false,
        };
        _session = new InferenceSession(onnxModelPath, sessionOptions);

        using var configDoc = JsonDocument.Parse(File.ReadAllText(onnxConfigPath));
        var root = configDoc.RootElement;

        _phonemeIdMap = new Dictionary<string, int>();
        foreach (var prop in root.GetProperty("phoneme_id_map").EnumerateObject())
            _phonemeIdMap[prop.Name] = prop.Value[0].GetInt32();

        SampleRate = root.GetProperty("audio").GetProperty("sample_rate").GetInt32();
        EspeakVoice = root.GetProperty("espeak").GetProperty("voice").GetString()
            ?? throw new InvalidOperationException($"{onnxConfigPath}: espeak.voice is missing");

        var inference = root.GetProperty("inference");
        _noiseScale = inference.GetProperty("noise_scale").GetSingle();
        _lengthScale = inference.GetProperty("length_scale").GetSingle();
        _noiseW = inference.GetProperty("noise_w").GetSingle();
    }

    public byte[] SynthesizeWav(string phonemes)
    {
        var ids = BuildPhonemeIds(phonemes);

        var inputTensor = new DenseTensor<long>(ids.ToArray(), new[] { 1, ids.Count });
        var inputLengthsTensor = new DenseTensor<long>(new long[] { ids.Count }, new[] { 1 });
        var scalesTensor = new DenseTensor<float>(new[] { _noiseScale, _lengthScale, _noiseW }, new[] { 3 });
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("input_lengths", inputLengthsTensor),
            NamedOnnxValue.CreateFromTensor("scales", scalesTensor)
        };

        using var results = _session.Run(inputs);
        var audio = results.First().AsEnumerable<float>().ToArray();
        return EncodeWav(audio, SampleRate);
    }

    // Piper's own convention (rhasspy/piper voice.py): beginning-of-sequence id, then each
    // phoneme id followed immediately by the pad id (including after the very last phoneme,
    // right before the end-of-sequence id), symbols absent from this voice's map are dropped
    // rather than failing the whole request - matches how Piper itself behaves on unknown input.
    private List<long> BuildPhonemeIds(string phonemes)
    {
        var ids = new List<long> { _phonemeIdMap["^"] };
        foreach (var rune in phonemes.EnumerateRunes())
        {
            if (!_phonemeIdMap.TryGetValue(rune.ToString(), out var id)) continue;
            ids.Add(id);
            ids.Add(_phonemeIdMap["_"]);
        }
        ids.Add(_phonemeIdMap["$"]);
        return ids;
    }

    private static byte[] EncodeWav(float[] samples, int sampleRate)
    {
        short[] pcm = samples.Select(s => (short)Math.Clamp(s * short.MaxValue, short.MinValue, short.MaxValue)).ToArray();
        int dataSize = pcm.Length * 2;

        using var ms = new MemoryStream(44 + dataSize);
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + dataSize);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);         // PCM
            w.Write((short)1);         // mono
            w.Write(sampleRate);
            w.Write(sampleRate * 2);   // byte rate (mono, 16-bit)
            w.Write((short)2);         // block align
            w.Write((short)16);        // bits per sample
            w.Write("data"u8);
            w.Write(dataSize);
            foreach (var s in pcm) w.Write(s);
        }
        return ms.ToArray();
    }

    public void Dispose() => _session.Dispose();
}
