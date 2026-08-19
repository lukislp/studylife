using System.Text;
using System.Text.RegularExpressions;

namespace StudyLife.Tts;

/// <summary>
/// Splits text into bounded-size pieces for TTS synthesis. A single ONNX inference call's
/// memory cost grows much faster than linearly with phoneme-sequence length - confirmed live
/// in production: a several-thousand-character note OOMKilled every pod at a memory limit that
/// comfortably handled short notes. Keeping each individual synthesis call's input short bounds
/// peak memory regardless of how long the overall note is.
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

            var current = new StringBuilder();
            foreach (var sentence in SentenceSplit().Split(line))
            {
                if (sentence.Length == 0) continue;

                // A "sentence" can itself exceed the cap (e.g. a long unpunctuated table row) -
                // hard word-wrap it so no chunk is ever unbounded regardless of input shape.
                foreach (var piece in WrapIfTooLong(sentence, maxChunkLength))
                {
                    if (current.Length > 0 && current.Length + piece.Length + 1 > maxChunkLength)
                    {
                        yield return current.ToString().Trim();
                        current.Clear();
                    }
                    current.Append(piece).Append(' ');
                }
            }
            if (current.Length > 0) yield return current.ToString().Trim();
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
