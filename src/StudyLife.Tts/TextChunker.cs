using System.Text;
using System.Text.RegularExpressions;

namespace StudyLife.Tts;

/// <summary>
/// Splits text into one piece per sentence for TTS synthesis - two independent reasons, not
/// just one:
/// 1. Memory: a single ONNX inference call's cost grows much faster than linearly with phoneme
///    sequence length - confirmed live in production, a several-thousand-character note
///    OOMKilled every pod at a memory limit that comfortably handled short notes.
/// 2. Pacing: espeak-ng's IPA phonemization (EspeakPhonemizer) strips punctuation entirely -
///    verified directly, "Hallo, das ist ein Test." phonemizes with no comma/period symbol
///    anywhere in the output. Piper therefore gets no pause cue from punctuation at all, so
///    multiple sentences fed into one synthesis call run together with no break. Splitting per
///    sentence (never batching several into one chunk, even when short) and inserting silence
///    between the resulting audio segments (PiperVoice.SynthesizeWav) is what actually produces
///    a pause at every sentence boundary - the memory bound is a side effect of the same split,
///    not the only reason for it.
/// </summary>
public static partial class TextChunker
{
    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplit();

    public static IEnumerable<string> Chunk(string text, int maxChunkLength = 300)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            foreach (var sentence in SentenceSplit().Split(line))
            {
                if (sentence.Length == 0) continue;

                // A "sentence" can itself exceed the cap (e.g. a long unpunctuated table row) -
                // hard word-wrap it so no chunk is ever unbounded regardless of input shape.
                foreach (var piece in WrapIfTooLong(sentence, maxChunkLength))
                    yield return piece.Trim();
            }
        }
    }

    private static IEnumerable<string> WrapIfTooLong(string sentence, int maxChunkLength)
    {
        if (sentence.Length <= maxChunkLength)
        {
            yield return sentence;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + word.Length + 1 > maxChunkLength)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
            current.Append(word).Append(' ');
        }
        if (current.Length > 0) yield return current.ToString().Trim();
    }
}
