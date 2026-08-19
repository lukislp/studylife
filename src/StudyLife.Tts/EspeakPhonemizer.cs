using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace StudyLife.Tts;

/// <summary>
/// Text -> IPA phonemes via libespeak-ng (P/Invoke, no Python/CLI subprocess involved).
/// </summary>
public sealed class EspeakPhonemizer
{
    private static readonly object Lock = new();
    private static bool _initialized;

    static EspeakPhonemizer()
    {
        NativeLibrary.SetDllImportResolver(typeof(EspeakPhonemizer).Assembly, ResolveEspeak);
    }

    // apt installs the *versioned* libespeak-ng.so.1, not the unversioned libespeak-ng.so
    // (that symlink only ships in the -dev package, which the runtime image has no reason
    // to carry) - resolved explicitly instead of relying on the default DllImport probing.
    private static IntPtr ResolveEspeak(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "espeak-ng") return IntPtr.Zero;
        foreach (var candidate in new[] { "libespeak-ng.so.1", "libespeak-ng.so", "espeak-ng" })
        {
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        }
        return IntPtr.Zero;
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Lock)
        {
            if (_initialized) return;
            const int AudioOutputSynchronous = 2;
            const int EspeakInitializePhonemeEvents = 0x0001;
            const int EspeakInitializeDontExit = 0x8000;
            int rate = NativeMethods.espeak_Initialize(AudioOutputSynchronous, 0, null,
                EspeakInitializePhonemeEvents | EspeakInitializeDontExit);
            if (rate <= 0)
                throw new InvalidOperationException("espeak_Initialize failed - is libespeak-ng.so.1 installed?");
            _initialized = true;
        }
    }

    /// <param name="espeakVoice">e.g. "de" - a Piper voice's config.json has this under espeak.voice.</param>
    public string Phonemize(string text, string espeakVoice)
    {
        EnsureInitialized();
        // espeak-ng's voice selection and phoneme buffer are global, process-wide state (not
        // per-call/per-instance) - two threads phonemizing in different languages at the same
        // time would otherwise corrupt each other's output, so select-voice-then-phonemize is
        // one critical section, not just each native call individually.
        lock (Lock)
        {
            var err = NativeMethods.espeak_SetVoiceByName(espeakVoice);
            if (err != 0)
                throw new InvalidOperationException($"espeak_SetVoiceByName(\"{espeakVoice}\") failed with error {err}");

            const int EspeakCharsUtf8 = 1;
            const int EspeakPhonemesIpa = 0x02;

            var utf8 = Encoding.UTF8.GetBytes(text + "\0");
            var unmanaged = Marshal.AllocHGlobal(utf8.Length);
            try
            {
                Marshal.Copy(utf8, 0, unmanaged, utf8.Length);
                var textPtr = unmanaged;
                var sb = new StringBuilder();
                // Each call consumes and phonemizes one clause; textPtr is advanced by
                // espeak-ng itself and becomes NULL once the whole input has been consumed.
                while (textPtr != IntPtr.Zero)
                {
                    var resultPtr = NativeMethods.espeak_TextToPhonemes(ref textPtr, EspeakCharsUtf8, EspeakPhonemesIpa);
                    if (resultPtr == IntPtr.Zero) continue;
                    sb.Append(Marshal.PtrToStringUTF8(resultPtr));
                    sb.Append(' ');
                }
                return sb.ToString().Trim();
            }
            finally
            {
                Marshal.FreeHGlobal(unmanaged);
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("espeak-ng", CallingConvention = CallingConvention.Cdecl)]
        public static extern int espeak_Initialize(int output, int buflength, string? path, int options);

        [DllImport("espeak-ng", CallingConvention = CallingConvention.Cdecl)]
        public static extern int espeak_SetVoiceByName(string name);

        [DllImport("espeak-ng", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr espeak_TextToPhonemes(ref IntPtr textptr, int textmode, int phonememode);
    }
}
