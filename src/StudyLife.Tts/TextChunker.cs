using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace StudyLife.Tts;

/// <summary>One piece of text ready for synthesis, with the pause that should follow it.</summary>
public readonly record struct TextChunk(string Text, bool LongPause);

/// <summary>
/// Splits text into one piece per clause or sentence (see ShortPauseEnders/the sentence-ender
/// fallback below for the exact punctuation) for TTS synthesis - two independent reasons, not
/// just one:
/// 1. Memory: a single ONNX inference call's cost grows much faster than linearly with phoneme
///    sequence length - confirmed live in production, a several-thousand-character note
///    OOMKilled every pod at a memory limit that comfortably handled short notes.
/// 2. Pacing: espeak-ng's IPA phonemization (EspeakPhonemizer) strips punctuation entirely -
///    verified directly, "Hallo, das ist ein Test." phonemizes with no comma/period symbol
///    anywhere in the output. Piper therefore gets no pause cue from punctuation at all, so
///    multiple clauses/sentences fed into one synthesis call run together with no break at
///    all. Splitting per clause (never batching several into one chunk, even when short) and
///    inserting silence between the resulting audio segments (PiperVoice.SynthesizeWav) is
///    what actually produces a pause at every clause/sentence boundary - the memory bound is a
///    side effect of the same split, not the only reason for it.
/// </summary>
public static partial class TextChunker
{
    // Any chunk not ending in one of the short-pause characters below gets the long pause -
    // covers real sentence enders (., !, ?, and the ellipsis … U+2026 - a single character,
    // distinct from three separate periods, but just as much a full stop pause-wise) AND
    // chunks with no trailing punctuation at all (a hard-wrapped table row, a heading with no
    // final period) alike, since both are real structural breaks either way.
    //
    // Short pause: clause-level punctuation. Deliberately NOT the plain ASCII hyphen "-" -
    // that's overwhelmingly used inside compound words in this app's languages (German
    // especially: "Wort-Vektorisierung", "Skip-Gram", "Co-Occurrence" all appear in real note
    // content) and splitting on every one of those would fragment normal words, not pause at
    // real breaks. The em/en dash (—/–, distinct Unicode characters) are real parenthetical-
    // aside punctuation and are included; a plain hyphen is not.
    private static readonly SearchValues<char> ShortPauseEnders = SearchValues.Create(",;:—–)]");

    // Split after any pause-worthy character, long or short (see above). The closing "]" must
    // be escaped inside the character class - unescaped, it would close the class early and
    // silently turn this into a pattern that (almost) never matches real text.
    [GeneratedRegex(@"(?<=[.!?…,;:—–)\]])\s+")]
    private static partial Regex ClauseSplit();

    public static IEnumerable<TextChunk> Chunk(string text, int maxChunkLength = 300)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            foreach (var clause in ClauseSplit().Split(line))
            {
                if (clause.Length == 0) continue;

                // A clause can itself exceed the cap (e.g. a long unpunctuated table row) -
                // hard word-wrap it so no chunk is ever unbounded regardless of input shape.
                foreach (var piece in WrapIfTooLong(clause, maxChunkLength))
                {
                    var trimmed = piece.Trim();
                    if (trimmed.Length == 0) continue;
                    var longPause = !ShortPauseEnders.Contains(trimmed[^1]);
                    yield return new TextChunk(trimmed, longPause);
                }
            }
        }
    }

    private static IEnumerable<string> WrapIfTooLong(string clause, int maxChunkLength)
    {
        if (clause.Length <= maxChunkLength)
        {
            yield return clause;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var word in clause.Split(' ', StringSplitOptions.RemoveEmptyEntries))
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
