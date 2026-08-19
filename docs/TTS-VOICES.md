# Text-to-speech voice models

The "read note aloud" feature synthesizes speech using [Piper](https://github.com/rhasspy/piper)
voice models, pulled from the [rhasspy/piper-voices](https://huggingface.co/rhasspy/piper-voices)
collection on Hugging Face at Docker image build time (see `src/StudyLife.Server/Dockerfile`) -
not checked into this repository. Languages without a baked-in voice fall back to the browser's
own Web Speech API client-side, so the feature works for every supported UI language either way.

## Baked into the default image

| Language | Voice | License |
|---|---|---|
| German (de) | [de_DE-thorsten-low](https://huggingface.co/rhasspy/piper-voices/tree/main/de/de_DE/thorsten/low) | [CC0](https://github.com/thorstenMueller/Thorsten-Voice) - public domain, no conditions |
| English (en) | [en_US-amy-low](https://huggingface.co/rhasspy/piper-voices/tree/main/en/en_US/amy/low) | [CC-BY-SA-4.0](https://github.com/MycroftAI/mimic3-voices) - attribution + share-alike for the model itself; using it to generate speech is unrestricted |

Only these two are baked into the image today - see the Dockerfile comment for why (image size
trade-off). The other 19 languages with an available Piper voice (see the table below) are a
possible future addition; each would need the same license check before being added.

## Available but not yet baked in (Web Speech API fallback covers these today)

en_US alternate voices aside, a Piper voice exists on Hugging Face for: fr, es, it, pt, nl, da,
sv, fi, el, pl, cs, sk, hu, ro, bg, sl, lv, uk, ru (21 of the app's 26 languages total, including
de/en above). No Piper voice exists (as of this writing) for: hr, et, lt, mt, ga - the Web Speech
API fallback is the only option for these regardless of future additions, unless a suitable model
appears upstream.
