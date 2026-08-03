// Collocated JS isolation module for Focus.razor - index.html is intentionally left
// untouched, hence here instead of the global <script> block.
//
// Generates ambient background sounds purely programmatically via the Web Audio API - no
// external audio file, no CDN. Five types: pure noise (white/brown, unchanged since
// the first version), filtered/modulated noise for rain/ocean (deliberately doesn't sound
// like noise, even though it's technically built on it), and a genuine, tonal sound ("tone" -
// two gently beating sine tones, no noise component) for anyone who doesn't find "noise"
// relaxing enough on its own.

const STORAGE_KEY = 'studylife.focus.ambient';
const BUFFER_SECONDS = 4;

let audioCtx = null;
let gainNode = null;
// Everything the currently running sound has created as nodes (sources with .stop(),
// filters/oscillators only with .disconnect()) - fully torn down on every change/stop,
// instead of managing just a single sourceNode like before (rain/ocean/tone need
// several chained nodes at the same time).
let activeNodes = [];

function ensureContext() {
    if (!audioCtx) {
        audioCtx = new (window.AudioContext || window.webkitAudioContext)();
        gainNode = audioCtx.createGain();
        gainNode.connect(audioCtx.destination);
    }
    return audioCtx;
}

function createNoiseBuffer(ctx, type) {
    const length = Math.floor(ctx.sampleRate * BUFFER_SECONDS);
    const buffer = ctx.createBuffer(1, length, ctx.sampleRate);
    const data = buffer.getChannelData(0);
    if (type === 'brown') {
        // Random walk (integrator over white noise), standard technique for "brown"
        // noise. The factor of 3.5 compensates for the amplitude lost through the integration.
        let last = 0;
        for (let i = 0; i < length; i++) {
            const white = Math.random() * 2 - 1;
            last = (last + 0.02 * white) / 1.02;
            data[i] = last * 3.5;
        }
    } else {
        for (let i = 0; i < length; i++) {
            data[i] = Math.random() * 2 - 1;
        }
    }
    return buffer;
}

function makeNoiseSource(ctx, noiseType) {
    const src = ctx.createBufferSource();
    src.buffer = createNoiseBuffer(ctx, noiseType);
    src.loop = true;
    return src;
}

function teardownActiveNodes() {
    for (const node of activeNodes) {
        try { if (typeof node.stop === 'function') node.stop(); } catch (e) { /* already stopped */ }
        try { node.disconnect(); } catch (e) { /* already disconnected */ }
    }
    activeNodes = [];
}

function buildNoise(ctx, noiseType) {
    const src = makeNoiseSource(ctx, noiseType);
    src.connect(gainNode);
    activeNodes.push(src);
    src.start();
}

// Rain: white noise through a bandpass filter (emphasizes the "pattering" frequency range,
// cuts away the flat broadband character of pure noise) plus a gentle
// lowpass afterward so nothing sounds hissy/harsh.
function buildRain(ctx) {
    const src = makeNoiseSource(ctx, 'white');
    const bandpass = ctx.createBiquadFilter();
    bandpass.type = 'bandpass';
    bandpass.frequency.value = 2200;
    bandpass.Q.value = 0.6;
    const lowpass = ctx.createBiquadFilter();
    lowpass.type = 'lowpass';
    lowpass.frequency.value = 3500;
    src.connect(bandpass);
    bandpass.connect(lowpass);
    lowpass.connect(gainNode);
    activeNodes.push(src, bandpass, lowpass);
    src.start();
}

// Ocean: brown noise (already deeper/warmer by nature) through a lowpass whose
// cutoff frequency is swept up and down by a very slow LFO (0.08 Hz, ~12s per cycle) -
// simulates waves swelling and receding, instead of a static noise bed.
function buildOcean(ctx) {
    const src = makeNoiseSource(ctx, 'brown');
    const lowpass = ctx.createBiquadFilter();
    lowpass.type = 'lowpass';
    lowpass.frequency.value = 500;
    const lfo = ctx.createOscillator();
    lfo.type = 'sine';
    lfo.frequency.value = 0.08;
    const lfoGain = ctx.createGain();
    lfoGain.gain.value = 350; // range/swing of the cutoff-frequency modulation
    lfo.connect(lfoGain);
    lfoGain.connect(lowpass.frequency);
    src.connect(lowpass);
    lowpass.connect(gainNode);
    activeNodes.push(src, lowpass, lfo, lfoGain);
    src.start();
    lfo.start();
}

// Tone: deliberately WITHOUT any noise component - two sine tones slightly detuned against
// each other (root + fifth, classic drone/singing-bowl interval) with a slow LFO on
// the overall volume ("breathing"). For anyone who doesn't find noise relaxing
// enough on its own - this is the only non-noise-based sound here.
function buildTone(ctx) {
    const toneGain = ctx.createGain();
    toneGain.gain.value = 1;
    const root = ctx.createOscillator();
    root.type = 'sine';
    root.frequency.value = 110; // A2
    const fifth = ctx.createOscillator();
    fifth.type = 'sine';
    fifth.frequency.value = 164.81; // E3, pure fifth above A2
    const rootGain = ctx.createGain();
    rootGain.gain.value = 0.6;
    const fifthGain = ctx.createGain();
    fifthGain.gain.value = 0.35;
    root.connect(rootGain);
    fifth.connect(fifthGain);
    rootGain.connect(toneGain);
    fifthGain.connect(toneGain);

    const breathLfo = ctx.createOscillator();
    breathLfo.type = 'sine';
    breathLfo.frequency.value = 0.06; // ~16s per breath
    const breathDepth = ctx.createGain();
    breathDepth.gain.value = 0.2;
    breathLfo.connect(breathDepth);
    breathDepth.connect(toneGain.gain);

    toneGain.connect(gainNode);
    activeNodes.push(root, fifth, rootGain, fifthGain, breathLfo, breathDepth, toneGain);
    root.start();
    fifth.start();
    breathLfo.start();
}

export function playAmbient(type, volume) {
    if (type === 'off') { stopAmbient(); return; }
    const ctx = ensureContext();
    if (ctx.state === 'suspended') ctx.resume().catch(() => { /* autoplay policy without a user gesture, ignore */ });
    teardownActiveNodes();
    gainNode.gain.setValueAtTime(volume, ctx.currentTime);

    switch (type) {
        case 'rain': buildRain(ctx); break;
        case 'ocean': buildOcean(ctx); break;
        case 'tone': buildTone(ctx); break;
        default: buildNoise(ctx, type); break; // 'white' | 'brown'
    }
}

export function stopAmbient() {
    teardownActiveNodes();
}

export function setAmbientVolume(volume) {
    if (gainNode && audioCtx) gainNode.gain.setValueAtTime(volume, audioCtx.currentTime);
}

export function loadAmbientPrefs() {
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        if (!raw) return null;
        return JSON.parse(raw);
    } catch (e) {
        return null; // localStorage not available (e.g. private mode), use default
    }
}

export function saveAmbientPrefs(type, volume) {
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify({ type, volume }));
    } catch (e) { /* quota/private mode, preference gets lost - not critical */ }
}

export function disposeAmbient() {
    teardownActiveNodes();
    if (audioCtx) {
        try { audioCtx.close(); } catch (e) { /* ignore */ }
        audioCtx = null;
        gainNode = null;
    }
}
