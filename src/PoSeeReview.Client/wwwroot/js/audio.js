// Programmatic sound. No sample files, no asset fetches — every sound here is oscillators and
// filtered noise built at call time, so the whole audible identity of the app costs a few KB of
// source and zero network bytes.
//
// Two rules shape the design:
//
//  1. An AudioContext created before a user gesture starts 'suspended' and never recovers on
//     its own. So the context is created lazily on the first real interaction, and every play
//     call is a no-op until then rather than an error.
//  2. Sound defaults OFF: the simplest app is a quiet one. It is switched on in /settings,
//     haptics inherit the preference, and an explicit reduced-motion request still forces silence.

import { gfx } from './gfx-core.js';

const STORAGE_KEY = 'posee_audio_enabled';
const VOLUME_KEY = 'posee_audio_volume';
const MAP_SOUNDS_KEY = 'posee_map_sounds';

// Headroom: several voices can overlap during the score reveal, and summing oscillators at unity
// gain clips hard. The user's volume scales this, never replaces it.
const MASTER_TRIM = 0.22;

// How long unlock() will wait for a resume that may never arrive. See the note in unlock().
const RESUME_GRACE_MS = 200;

const state = {
    ctx: null,
    master: null,      // Final trim. Everything meets here.
    dry: null,         // Panned, unreverberated signal.
    reverbSend: null,  // Shared send bus into the convolver.
    convolver: null,
    wet: null,
    analyser: null,    // Tap off the master bus, read by level(). Not on the audible path.
    levelBuffer: null,
    enabled: true,     // The stored preference. on() is what is actually in force.
    silenced: false,   // Reduced motion. A runtime gate: it never rewrites the preference.
    volume: 1,
    mapSounds: false,  // Opt-in even with sound on: wind, sonar and hum on the map.
    unlocked: false,
    // Guards against a burst of identical sounds (a fast count-up) stacking into clipping.
    lastPlayedAt: new Map()
};

// Only an explicit "true" is on: an absent key, or a browser that blocks localStorage, is quiet.
function readStoredEnabled() {
    try {
        return localStorage.getItem(STORAGE_KEY) === 'true';
    } catch {
        return false;
    }
}

function readStoredVolume() {
    try {
        const stored = parseFloat(localStorage.getItem(VOLUME_KEY));
        return Number.isFinite(stored) ? Math.max(0, Math.min(1, stored)) : 1;
    } catch {
        return 1;
    }
}

const on = () => state.enabled && !state.silenced;

/**
 * A room, synthesised rather than downloaded. Exponentially decaying stereo noise is the
 * textbook cheap impulse response; the two channels are decorrelated (independent noise, and a
 * slightly different decay constant) because identical channels collapse the reverb to the
 * centre and undo the panning it is supposed to widen.
 *
 * Two early reflections are stamped in on top. Without them the tail reads as a hiss rather than
 * a space — the early part is what the ear uses to judge room size.
 */
function buildImpulseResponse(ctx, seconds = 1.3, decay = 3.2) {
    const length = Math.max(1, Math.floor(ctx.sampleRate * seconds));
    const impulse = ctx.createBuffer(2, length, ctx.sampleRate);

    for (let channel = 0; channel < 2; channel++) {
        const data = impulse.getChannelData(channel);
        const channelDecay = decay * (channel === 0 ? 1 : 1.07);
        for (let i = 0; i < length; i++) {
            const t = i / length;
            data[i] = (Math.random() * 2 - 1) * Math.pow(1 - t, channelDecay);
        }
        // Early reflections at ~11ms and ~23ms, offset per channel so they do not sum to mono.
        for (const [ms, amplitude] of [[11, 0.34], [23, 0.19]]) {
            const index = Math.floor(ctx.sampleRate * (ms + channel * 3) / 1000);
            if (index < length) {
                data[index] += amplitude * (channel === 0 ? 1 : -1);
            }
        }
    }
    return impulse;
}

function ensureContext() {
    if (state.ctx) {
        return state.ctx;
    }

    const Ctor = window.AudioContext || window.webkitAudioContext;
    if (!Ctor) {
        return null;
    }

    try {
        state.ctx = new Ctor({ latencyHint: 'interactive' });
        const ctx = state.ctx;

        state.master = ctx.createGain();
        state.master.gain.value = MASTER_TRIM * state.volume;

        state.master.connect(ctx.destination);

        state.analyser = ctx.createAnalyser();
        state.analyser.fftSize = 256;
        state.levelBuffer = new Float32Array(state.analyser.fftSize);
        state.master.connect(state.analyser);

        // Dry path: every voice pans into here.
        state.dry = ctx.createGain();
        state.dry.gain.value = 1;
        state.dry.connect(state.master);

        // Wet path. The send is shared by every voice, so the room is built once rather than
        // per note — a convolver per voice would be hundreds of convolutions during a count-up.
        state.reverbSend = ctx.createGain();
        state.reverbSend.gain.value = 1;

        state.convolver = ctx.createConvolver();
        state.convolver.normalize = true;
        state.convolver.buffer = buildImpulseResponse(ctx);

        state.wet = ctx.createGain();
        state.wet.gain.value = 0.30;

        state.reverbSend.connect(state.convolver);
        state.convolver.connect(state.wet);
        state.wet.connect(state.master);
    } catch {
        state.ctx = null;
    }

    return state.ctx;
}

/**
 * Per-voice output stage: pan, then split to the dry bus and the shared reverb send.
 * StereoPannerNode rather than PannerNode — this is a flat 2D interface, so an HRTF panner would
 * spend real CPU modelling a head for positions that only ever vary along one axis.
 */
function makeOutput(pan = 0, send = 0.25) {
    const ctx = state.ctx;
    const panner = ctx.createStereoPanner
        ? ctx.createStereoPanner()
        : null;

    const head = panner ?? ctx.createGain();
    if (panner) {
        panner.pan.value = Math.max(-1, Math.min(1, pan));
    }

    head.connect(state.dry);

    let sendGain = null;
    if (send > 0) {
        sendGain = ctx.createGain();
        sendGain.gain.value = send;
        head.connect(sendGain);
        sendGain.connect(state.reverbSend);
    }

    return {
        node: head,
        dispose() {
            try { head.disconnect(); } catch { /* already torn down */ }
            if (sendGain) {
                try { sendGain.disconnect(); } catch { /* already torn down */ }
            }
        }
    };
}

function canPlay() {
    return on() && state.unlocked && state.ctx && state.ctx.state === 'running';
}

/** Rate-limits one sound key so rapid repeats thin out instead of piling up. */
function throttled(key, minGapMs) {
    const now = performance.now();
    const last = state.lastPlayedAt.get(key) ?? -Infinity;
    if (now - last < minGapMs) {
        return true;
    }
    state.lastPlayedAt.set(key, now);
    return false;
}

/**
 * One synth voice: an oscillator through its own gain envelope.
 * The envelope always ends at an explicit zero — a note left at a non-zero gain keeps its
 * oscillator alive and audible forever.
 */
function voice({ type = 'sine', freq, startFreq, endFreq, attack = 0.005, decay = 0.18, peak = 1, delay = 0, detune = 0, pan = 0, send = 0.25 }) {
    const ctx = state.ctx;
    const t0 = ctx.currentTime + delay;

    const osc = ctx.createOscillator();
    osc.type = type;
    osc.detune.value = detune;

    if (startFreq && endFreq) {
        osc.frequency.setValueAtTime(startFreq, t0);
        osc.frequency.exponentialRampToValueAtTime(Math.max(1, endFreq), t0 + attack + decay);
    } else {
        osc.frequency.setValueAtTime(freq, t0);
    }

    const gain = ctx.createGain();
    gain.gain.setValueAtTime(0.0001, t0);
    gain.gain.exponentialRampToValueAtTime(Math.max(0.0001, peak), t0 + attack);
    gain.gain.exponentialRampToValueAtTime(0.0001, t0 + attack + decay);

    const output = makeOutput(pan, send);
    osc.connect(gain);
    gain.connect(output.node);

    osc.start(t0);
    osc.stop(t0 + attack + decay + 0.02);
    osc.onended = () => {
        // Explicit teardown: WebAudio nodes are not collected while connected, and this app can
        // fire hundreds of these during a single score reveal. The panner and send are part of
        // that chain now, so they have to be released here too or the graph grows unboundedly.
        osc.disconnect();
        gain.disconnect();
        output.dispose();
    };
}

/** Short filtered-noise burst — used for texture (paper, ink, impact) rather than pitch. */
function noise({ duration = 0.12, peak = 0.5, filterHz = 1800, filterType = 'lowpass', delay = 0, pan = 0, send = 0.35 }) {
    const ctx = state.ctx;
    const t0 = ctx.currentTime + delay;
    const frameCount = Math.max(1, Math.floor(ctx.sampleRate * duration));

    const buffer = ctx.createBuffer(1, frameCount, ctx.sampleRate);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < frameCount; i++) {
        data[i] = Math.random() * 2 - 1;
    }

    const source = ctx.createBufferSource();
    source.buffer = buffer;

    const filter = ctx.createBiquadFilter();
    filter.type = filterType;
    filter.frequency.value = filterHz;

    const gain = ctx.createGain();
    gain.gain.setValueAtTime(Math.max(0.0001, peak), t0);
    gain.gain.exponentialRampToValueAtTime(0.0001, t0 + duration);

    const output = makeOutput(pan, send);
    source.connect(filter);
    filter.connect(gain);
    gain.connect(output.node);

    source.start(t0);
    source.onended = () => {
        source.disconnect();
        filter.disconnect();
        gain.disconnect();
        output.dispose();
    };
}

/** White noise of a given length, for sources that need their own filter sweep. */
function noiseBuffer(seconds) {
    const ctx = state.ctx;
    const frameCount = Math.max(1, Math.floor(ctx.sampleRate * seconds));
    const buffer = ctx.createBuffer(1, frameCount, ctx.sampleRate);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < frameCount; i++) {
        data[i] = Math.random() * 2 - 1;
    }
    return buffer;
}

/**
 * Maps a screen x coordinate to a stereo position. Clamped to ±0.85 rather than ±1: a sound
 * hard-panned to one channel disappears entirely on a phone held with one speaker covered, and
 * on headphones it sits outside the head rather than in the scene.
 */
function panForClientX(clientX) {
    const width = window.innerWidth || 1;
    const normalised = (clientX / width) * 2 - 1;
    return Math.max(-0.85, Math.min(0.85, normalised));
}

// A pentatonic set, so any combination of these is consonant. The score count-up plays notes in
// effectively random order; on a diatonic scale that produces semitone clashes.
const PENTATONIC = [523.25, 587.33, 698.46, 783.99, 932.33]; // C5 D5 F5 G5 A#5

// ── Seeded generation, for the per-comic signature (and soundtrack.js) ───────────────────

/**
 * FNV-1a over a string. Any stable hash would do; this one is four lines and has no collisions
 * that matter at the scale of "one motif per restaurant".
 */
export function hashString(text) {
    let hash = 0x811c9dc5;
    const value = String(text ?? '');
    for (let i = 0; i < value.length; i++) {
        hash ^= value.charCodeAt(i);
        hash = Math.imul(hash, 0x01000193);
    }
    return hash >>> 0;
}

/** mulberry32 — a small, fast, well-distributed PRNG. Deterministic from its seed, which is the point. */
export function seededRandom(seed) {
    let a = seed >>> 0;
    return () => {
        a = (a + 0x6d2b79f5) >>> 0;
        let t = a;
        t = Math.imul(t ^ (t >>> 15), t | 1);
        t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

/**
 * Scale degrees in semitones, indexed by how strange the comic is. A place scoring 20 gets a
 * major pentatonic; one scoring 95 gets an octatonic set full of tritones. The scale is the
 * single biggest carrier of "how weird is this" — bigger than tempo, bigger than timbre — which
 * is why it is chosen by score rather than by the seed.
 */
export const SCALES = [
    [0, 2, 4, 7, 9],           // major pentatonic — mundane
    [0, 2, 3, 7, 9],           // minor pentatonic — a bit off
    [0, 2, 3, 5, 7, 8, 10],    // natural minor — unsettled
    [0, 1, 3, 4, 6, 7, 9, 10], // octatonic — genuinely strange
    [0, 1, 4, 6, 7, 10]        // no name worth having — reserved for the top band
];

export const SEMITONE = Math.pow(2, 1 / 12);

/**
 * A restaurant's four-note figure. The seed picks the notes and the contour; the SCORE picks the
 * scale, the tempo and the timbre — so two restaurants sound different from each other, and a
 * strange one sounds strange rather than merely different.
 */
function motif(seed, score = 50, { delay = 0, pan = null } = {}) {
    const strange = Math.min(1, Math.max(0, score / 100));
    const random = seededRandom(hashString(seed));

    const scale = SCALES[Math.min(SCALES.length - 1, Math.floor(strange * SCALES.length))];

    // Root within a fifth of A3, chosen by the seed. Keeping every motif in one octave means
    // two places played back to back are comparable rather than merely different in register.
    const rootSemitone = Math.floor(random() * 8) - 3;
    const root = 220 * Math.pow(SEMITONE, rootSemitone);

    // Faster as it gets stranger. 96bpm at zero, ~184 at 100.
    const beat = 60 / (96 + strange * 88) / 2;

    // Timbre tracks strangeness too: a sine is inoffensive, a sawtooth is not.
    const type = strange < 0.33 ? 'sine' : strange < 0.66 ? 'triangle' : 'sawtooth';

    const noteCount = 4;

    for (let i = 0; i < noteCount; i++) {
        const degree = Math.floor(random() * scale.length);
        // The last note jumps an octave a third of the time — a motif that ends where it sat
        // is a scale fragment, not a phrase.
        const octave = (i === noteCount - 1 && random() < 0.34) ? 12 : 0;
        const freq = root * Math.pow(SEMITONE, scale[degree] + octave);

        voice({
            type,
            freq,
            attack: 0.006,
            decay: beat * (i === noteCount - 1 ? 3.2 : 1.5),
            peak: 0.20 - i * 0.015,
            delay: delay + i * beat,
            // Slight detune that grows with strangeness: at the top of the range the motif is
            // audibly out of tune with itself.
            detune: (random() - 0.5) * strange * 34,
            // A caller that places the motif (the duel's call and response) narrows it around
            // that point; the random draw is still taken so the notes do not change.
            pan: pan === null ? (random() - 0.5) * 1.2 : pan + (random() - 0.5) * 0.3,
            send: 0.2 + strange * 0.4
        });
    }
}

/** Fades a gain to silence, then stops and disconnects every node of a long-running voice. */
function release(gain, nodes, seconds = 0.3) {
    const ctx = state.ctx;
    const t0 = ctx.currentTime;
    gain.gain.cancelScheduledValues(t0);
    gain.gain.setValueAtTime(Math.max(0.0001, gain.gain.value), t0);
    gain.gain.exponentialRampToValueAtTime(0.0001, t0 + seconds);
    setTimeout(() => {
        for (const node of nodes) {
            try { node.stop?.(); } catch { /* never started, or already stopped */ }
            try { node.disconnect(); } catch { /* already torn down */ }
        }
    }, seconds * 1000 + 60);
}

// ── The generation wait ──────────────────────────────────────────────────────────────────
//
// Ten seconds, most of it the image call. Each pipeline phase adds one voice to a soft chord
// and opens its filter a little, with a one-shot texture for what that phase is doing (a page
// turning, a thought, a pencil, lettering, a stamp). The bed never resolves on its own: the
// score roll that follows a finished comic is the resolution, and a failure sags it.

const WAIT_ROOT = 110;                 // A2
const WAIT_CHORD = [0, 7, 12, 16, 19]; // root, fifth, octave, major tenth, twelfth
const WAIT_MAX_MS = 90_000;            // A stream that never ends must not hum forever.
let wait = null;

function waitTexture(index) {
    const at = (s) => s + Math.random() * 0.03;
    switch (index) {
        case 0: // pages turning
            noise({ duration: 0.16, peak: 0.12, filterHz: 2600, filterType: 'bandpass', pan: -0.3, send: 0.3 });
            noise({ duration: 0.14, peak: 0.09, filterHz: 3200, filterType: 'bandpass', delay: at(0.2), pan: 0.3, send: 0.3 });
            break;
        case 1: // a thought
            voice({ type: 'sine', freq: 880, attack: 0.004, decay: 0.08, peak: 0.12, pan: -0.2, send: 0.4 });
            voice({ type: 'sine', freq: 1318.51, attack: 0.004, decay: 0.16, peak: 0.1, delay: 0.09, pan: 0.2, send: 0.45 });
            break;
        case 2: // pencil on paper
            for (let i = 0; i < 6; i++) {
                noise({ duration: 0.03 + Math.random() * 0.03, peak: 0.08, filterHz: 4200, filterType: 'highpass',
                    delay: at(i * 0.09), pan: (Math.random() - 0.5) * 0.6, send: 0.12 });
            }
            break;
        case 3: // lettering
            for (let i = 0; i < 4; i++) {
                voice({ type: 'square', freq: 1900 + i * 60, attack: 0.001, decay: 0.018, peak: 0.05, delay: at(i * 0.11), pan: -0.3 + i * 0.2, send: 0.1 });
            }
            break;
        default: // the press
            voice({ type: 'sine', startFreq: 140, endFreq: 60, attack: 0.002, decay: 0.2, peak: 0.3, send: 0.2 });
            noise({ duration: 0.1, peak: 0.1, filterHz: 1200, send: 0.2 });
    }
}

function addWaitVoice(index) {
    const ctx = state.ctx;
    const osc = ctx.createOscillator();
    osc.type = index === 0 ? 'sine' : 'triangle';
    osc.frequency.value = WAIT_ROOT * Math.pow(SEMITONE, WAIT_CHORD[index % WAIT_CHORD.length]);
    osc.detune.value = (Math.random() - 0.5) * 10;
    const gain = ctx.createGain();
    gain.gain.setValueAtTime(0.0001, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.5 / (1 + index * 0.4), ctx.currentTime + 0.8);
    osc.connect(gain);
    gain.connect(wait.filter);
    osc.start();
    wait.nodes.push(osc, gain);
    wait.oscillators.push(osc);
}

function endWaitBed(outcome) {
    if (!wait) return;
    const bed = wait;
    wait = null;
    clearTimeout(bed.timer);
    if (!state.ctx) return;
    // A failure sags a whole tone before it goes; success and abandonment just let go.
    if (outcome === 'fail') {
        for (const osc of bed.oscillators) {
            osc.detune.setTargetAtTime(osc.detune.value - 200, state.ctx.currentTime, 0.18);
        }
    }
    release(bed.gain, [...bed.nodes, bed.filter, bed.gain, bed.output.node], outcome === 'abandon' ? 0.15 : 0.6);
    setTimeout(() => bed.output.dispose(), 800);
}

// ── Map sounds (opt-in) ──────────────────────────────────────────────────────────────────

let wind = null;
let hum = null;

function readStoredMapSounds() {
    try {
        return localStorage.getItem(MAP_SOUNDS_KEY) === 'true';
    } catch {
        return false;
    }
}

const mapAudible = () => state.mapSounds && canPlay();

export const audio = {
    /**
     * Reads the stored preferences. Does NOT create a context — that needs a user gesture.
     * Call after gfx.init(): reduced motion is read from it, as "no unrequested stimulus".
     */
    init() {
        state.enabled = readStoredEnabled();
        state.volume = readStoredVolume();
        state.mapSounds = readStoredMapSounds();
        state.silenced = gfx.describe().reducedMotion;
        return { enabled: on(), unlocked: state.unlocked };
    },

    /** What is in force, not what is stored: false under reduced motion whatever the preference. */
    isEnabled: on,

    volume: () => state.volume,

    setVolume(volume) {
        const value = Number(volume);
        if (!Number.isFinite(value)) return state.volume;

        state.volume = Math.max(0, Math.min(1, value));
        try {
            localStorage.setItem(VOLUME_KEY, String(state.volume));
        } catch {
            // Session-only preference.
        }
        if (state.master) {
            // Approached, not set: a stepped gain under a sounding note is an audible click.
            state.master.gain.setTargetAtTime(MASTER_TRIM * state.volume, state.ctx.currentTime, 0.03);
        }
        return state.volume;
    },

    /**
     * Peak of what is on the master bus right now, 0..1. Read once a frame by the backdrop, so it
     * allocates nothing and answers 0 without touching the graph when nothing can be sounding.
     */
    level() {
        if (!canPlay() || !state.analyser?.getFloatTimeDomainData) return 0;
        const data = state.levelBuffer;
        state.analyser.getFloatTimeDomainData(data);
        let peak = 0;
        for (let i = 0; i < data.length; i++) {
            const sample = Math.abs(data[i]);
            if (sample > peak) peak = sample;
        }
        // A loud cue peaks around 0.12 after the master trim.
        return Math.min(1, peak * 8);
    },

    /**
     * Must be called from inside a real user-gesture handler. Browsers only allow an
     * AudioContext to enter 'running' from a trusted event; calling this from a timer or an
     * await continuation silently leaves it suspended.
     */
    async unlock() {
        if (!on()) {
            return false;
        }

        const ctx = ensureContext();
        if (!ctx) {
            return false;
        }

        try {
            if (ctx.state === 'suspended') {
                // Chrome will not start a context without user activation, and it does not reject:
                // the resume promise just stays pending until a gesture arrives — forever, if none
                // does. Nothing may wait on that. This is called from the discovery flow's own
                // async path (a remembered ZIP resumes the search on load, before any tap), so a
                // sound that has not started yet must not be able to stop a request being made.
                // The next real gesture unlocks properly; this call reports honestly either way.
                await Promise.race([
                    ctx.resume(),
                    new Promise(resolve => setTimeout(resolve, RESUME_GRACE_MS))
                ]);
            }
            state.unlocked = ctx.state === 'running';
        } catch {
            state.unlocked = false;
        }

        return state.unlocked;
    },

    async setEnabled(enabled) {
        state.enabled = !!enabled;
        try {
            localStorage.setItem(STORAGE_KEY, String(state.enabled));
        } catch {
            // Session-only preference.
        }

        if (on()) {
            await this.unlock();
        } else {
            if (state.ctx && state.ctx.state === 'running') {
                try {
                    await state.ctx.suspend();
                } catch {
                    // Suspension is best-effort; nothing further will be scheduled anyway.
                }
                state.unlocked = false;
            }
        }

        return on();
    },

    /**
     * Resolution chord when the count lands. Voiced by score: a low strangeness resolves to a
     * plain major triad, a high one adds a major seventh and a tritone above for unease.
     */
    scoreLand(score, delay = 0) {
        if (!canPlay()) return;

        const root = 261.63; // C4
        const strange = Math.min(1, Math.max(0, score / 100));

        // The chord is spread across the stereo field the way an ensemble is: root centred and
        // anchoring, upper voices to either side. Mono-stacked sines at these intervals beat
        // against each other in one spot and sound like a synth patch; spread, they sound wide.
        // The reverb send rises with strangeness, so a bizarre score resolves into a bigger,
        // stranger room than a mundane one.
        const room = 0.22 + strange * 0.45;

        voice({ type: 'sine', freq: root, attack: 0.01, decay: 0.9, peak: 0.5, delay, pan: 0, send: room });
        voice({ type: 'sine', freq: root * 1.5, attack: 0.01, decay: 0.85, peak: 0.36, delay: delay + 0.03, pan: -0.45, send: room });
        voice({ type: 'sine', freq: root * 2, attack: 0.01, decay: 0.8, peak: 0.30, delay: delay + 0.06, pan: 0.45, send: room });

        if (strange > 0.6) {
            // Major seventh — bright but unresolved.
            voice({ type: 'sine', freq: root * 1.888, attack: 0.02, decay: 0.7, peak: 0.22, delay: delay + 0.09, pan: -0.7, send: room });
        }
        if (strange > 0.85) {
            // Tritone. Deliberately uncomfortable; only the genuinely bizarre earns it. Panned
            // opposite the seventh so the two dissonances pull the image apart rather than
            // fighting in the centre.
            voice({ type: 'sine', freq: root * 1.414, attack: 0.02, decay: 0.65, peak: 0.18, delay: delay + 0.12, pan: 0.7, send: room });
        }

        noise({
            duration: 0.25, peak: 0.10, filterHz: 900 + strange * 2600,
            filterType: 'lowpass', delay: delay + 0.02, send: room
        });
    },

    /**
     * The whole count-up in one call: pentatonic ticks that climb and pan with the count, then the
     * landing chord, all placed on the context clock so they stay evenly spaced whatever the main
     * thread is doing.
     */
    scoreRoll(score, durationMs, { ticks: withTicks = true } = {}) {
        if (!canPlay() || throttled('roll', 300)) return;

        const seconds = Math.max(0, Math.min(5000, durationMs ?? 0)) / 1000;
        // One tick per ~60ms and never more than the number has values: a 3 does not rattle.
        // A sprung needle voices itself (see needle()), so it gets the chord without the ticks.
        const ticks = withTicks ? Math.max(0, Math.min(24, Math.round(seconds / 0.06), Math.floor(score))) : 0;

        for (let i = 0; i < ticks; i++) {
            const progress = (i + 1) / (ticks + 1);
            voice({
                type: 'square',
                freq: PENTATONIC[Math.min(PENTATONIC.length - 1, Math.floor(progress * PENTATONIC.length))],
                attack: 0.001,
                decay: 0.045,
                peak: 0.10 + progress * 0.06,
                delay: progress * seconds,
                pan: (progress * 2 - 1) * 0.5,
                send: 0.18
            });
        }

        this.scoreLand(score, seconds);
    },

    /** Short rising stinger on a completed share. Rises in pitch and travels left to right. */
    shareStinger() {
        if (!canPlay() || throttled('share', 800)) return;
        voice({ type: 'triangle', freq: 523.25, attack: 0.004, decay: 0.14, peak: 0.34, pan: -0.5, send: 0.28 });
        voice({ type: 'triangle', freq: 659.25, attack: 0.004, decay: 0.14, peak: 0.32, delay: 0.08, pan: 0, send: 0.3 });
        voice({ type: 'triangle', freq: 783.99, attack: 0.004, decay: 0.30, peak: 0.34, delay: 0.16, pan: 0.5, send: 0.36 });
        noise({ duration: 0.3, peak: 0.06, filterHz: 4200, filterType: 'highpass', delay: 0.16, pan: 0.3, send: 0.4 });
    },

    /**
     * Descending pair for errors — distinct from every rising cue above. Deliberately dry and
     * centred: an error that echoes around a large room reads as ambience, not as a problem.
     */
    error() {
        if (!canPlay() || throttled('error', 500)) return;
        voice({ type: 'sawtooth', freq: 311.13, attack: 0.006, decay: 0.18, peak: 0.22, pan: 0, send: 0.05 });
        voice({ type: 'sawtooth', freq: 233.08, attack: 0.006, decay: 0.34, peak: 0.24, delay: 0.11, pan: 0, send: 0.05 });
    },

    /**
     * Click panned to a raw viewport x coordinate. This is the form Blazor call sites can
     * actually use: MouseEventArgs already carries ClientX, whereas panning by element would
     * mean capturing an @ref on every button in a repeated list.
     */
    tapAt(clientX) {
        if (!canPlay() || throttled('tap', 40)) return;
        voice({
            type: 'triangle', freq: 660, attack: 0.002, decay: 0.05, peak: 0.35,
            pan: panForClientX(clientX), send: 0.14
        });
    },

    /**
     * The search going out. A sonar ping: one short tone, then a much quieter, longer, detuned
     * copy of itself behind the reverb send.
     *
     * The delayed copy is doing real work rather than decoration. The gap between the two is the
     * only thing that says "this is a request that has left and has not come back" — a single
     * blip is a button press, and the landing page's whole problem was that asking for your
     * location sounded exactly like tapping anything else. It is centred, because a search has
     * no direction until it returns.
     */
    locating() {
        if (!canPlay() || throttled('locating', 700)) return;

        voice({ type: 'sine', freq: 1174.66, attack: 0.003, decay: 0.16, peak: 0.24, pan: 0, send: 0.45 });
        voice({
            type: 'sine', freq: 1174.66, attack: 0.004, decay: 0.5, peak: 0.07,
            delay: 0.22, detune: -14, pan: 0, send: 0.7
        });
    },

    /**
     * Results landing. A rising figure whose LENGTH is the result count, capped at five notes.
     *
     * Mapping count to length rather than to pitch or volume is the point: "a lot came back" and
     * "barely anything came back" are the two states the user is about to act on, and a longer
     * run is legible as more without anyone having to be told what it means. It sweeps left to
     * right across the stereo field the way the grid fills.
     */
    arrival(count = 0) {
        if (!canPlay() || throttled('arrival', 600)) return;

        const notes = Math.max(2, Math.min(5, Math.round(count / 4)));
        const span = Math.max(1, notes - 1);

        for (let i = 0; i < notes; i++) {
            voice({
                type: 'triangle',
                freq: PENTATONIC[i % PENTATONIC.length],
                attack: 0.003,
                // The last note rings on. Without it the figure stops rather than arriving.
                decay: i === notes - 1 ? 0.34 : 0.1,
                peak: 0.18 + i * 0.012,
                delay: i * 0.065,
                pan: ((i / span) * 2 - 1) * 0.6,
                send: 0.22 + (i / span) * 0.2
            });
        }
    },

    /**
     * Nothing came back. Deliberately NOT `error()` — an empty result is not a failure, it is an
     * answer, and a search that returned zero restaurants should not sound like the app broke.
     * Two flat notes, no resolution, dry.
     */
    empty() {
        if (!canPlay() || throttled('empty', 700)) return;
        voice({ type: 'sine', freq: 392.0, attack: 0.006, decay: 0.2, peak: 0.16, pan: 0, send: 0.1 });
        voice({ type: 'sine', freq: 392.0, attack: 0.006, decay: 0.28, peak: 0.12, delay: 0.14, pan: 0, send: 0.12 });
    },

    /**
     * Tap on something already drawn, versus something that will have to be generated.
     *
     * These are one control apart in the same grid and they cost wildly different things — a
     * cached comic opens instantly and free, a fresh one spends a paid image call and about ten
     * seconds. The cached cue is bright and immediately resolved; the uncached one is lower,
     * slower, and leaves a note hanging, because something is now going to take a while.
     */
    tapCached(clientX) {
        if (!canPlay() || throttled('tap', 40)) return;
        const pan = panForClientX(clientX);
        voice({ type: 'triangle', freq: 880, attack: 0.002, decay: 0.05, peak: 0.3, pan, send: 0.14 });
        voice({ type: 'sine', freq: 1318.51, attack: 0.002, decay: 0.09, peak: 0.16, delay: 0.035, pan, send: 0.2 });
    },

    /** @see tapCached */
    tapUncached(clientX) {
        if (!canPlay() || throttled('tap', 40)) return;
        const pan = panForClientX(clientX);
        voice({ type: 'triangle', freq: 494, attack: 0.003, decay: 0.07, peak: 0.3, pan, send: 0.16 });
        voice({ type: 'sine', freq: 659.25, attack: 0.004, decay: 0.42, peak: 0.11, delay: 0.05, pan, send: 0.34 });
    },

    /**
     * One note of a comic's own motif, for a panel crossing the middle of the screen.
     *
     * Deliberately drawn from the SAME seeded sequence `signature` uses, so the notes a reader
     * hears while scrolling are the notes that played when the score landed — in the same order,
     * at their own pace. A separate random source would be a different tune about the same
     * restaurant, which is the one thing a per-place motif cannot afford to be.
     *
     * Long and soft. A reader scrolling is not waiting for a cue; this has to sit under what they
     * are doing, which is why it is a slow attack rather than the sharp blip `panelReveal` uses
     * during the reveal.
     */
    panelNote(seed, score, index, total) {
        if (!canPlay() || throttled(`panelNote${index}`, 400)) return;

        const strange = Math.min(1, Math.max(0, (score ?? 50) / 100));
        const random = seededRandom(hashString(seed));
        const scale = SCALES[Math.min(SCALES.length - 1, Math.floor(strange * SCALES.length))];

        const rootSemitone = Math.floor(random() * 8) - 3;
        const root = 220 * Math.pow(SEMITONE, rootSemitone);

        // Advance the sequence to this panel's position, so panel 2 gets the motif's second note
        // rather than its first. Re-seeding per call is what makes that reproducible.
        let degree = 0;
        for (let i = 0; i <= index; i++) {
            degree = Math.floor(random() * scale.length);
        }

        const span = Math.max(1, (total ?? 1) - 1);
        voice({
            type: strange < 0.5 ? 'sine' : 'triangle',
            freq: root * Math.pow(SEMITONE, scale[degree]) * 2,
            attack: 0.05,
            decay: 0.9,
            peak: 0.11,
            // Panned down the strip: the first panel from the left, the last from the right.
            pan: total > 1 ? ((index / span) * 2 - 1) * 0.5 : 0,
            send: 0.45
        });
    },

    /**
     * Moving between routes. Rises going deeper, falls coming back, and sweeps across the stereo
     * field in the direction of travel.
     *
     * Very quiet, and short. This fires on every internal navigation, so it has to be the sound
     * of a page turning rather than an event — anything with presence would become the loudest
     * thing in the app by sheer repetition. It starts panned to where the link was tapped, which
     * is what ties it to the thing the user actually touched.
     */
    navigate(direction, clientX) {
        if (!canPlay() || throttled('navigate', 250)) return;

        const forward = direction !== 'back';
        const from = panForClientX(clientX ?? window.innerWidth / 2);
        const to = forward ? Math.min(0.8, from + 0.5) : Math.max(-0.8, from - 0.5);

        voice({
            type: 'sine', freq: forward ? 523.25 : 659.25,
            attack: 0.004, decay: 0.09, peak: 0.09, pan: from, send: 0.2
        });
        voice({
            type: 'sine', freq: forward ? 659.25 : 523.25,
            attack: 0.004, decay: 0.14, peak: 0.07, delay: 0.05, pan: to, send: 0.28
        });
    },

    /**
     * A leaderboard row settling into a new rank: a short glide, up for a climb and down for a
     * fall, panned to the row. Barely there — a board can move several rows at once, and the
     * caller staggers them with `delay` (seconds) and caps how many sound.
     */
    rankMove(delta, clientX, delay = 0) {
        if (!canPlay() || !delta) return;
        const climbed = delta > 0;
        voice({
            type: 'sine', startFreq: climbed ? 440 : 587.33, endFreq: climbed ? 587.33 : 440,
            attack: 0.004, decay: 0.12, peak: 0.07, delay, pan: panForClientX(clientX), send: 0.22
        });
    },

    /** Confirmation for something added to a collection. Two rising ticks, deliberately small. */
    confirm() {
        if (!canPlay() || throttled('confirm', 300)) return;
        voice({ type: 'sine', freq: 783.99, attack: 0.003, decay: 0.09, peak: 0.26, pan: -0.2, send: 0.2 });
        voice({ type: 'sine', freq: 1046.5, attack: 0.003, decay: 0.16, peak: 0.22, delay: 0.07, pan: 0.2, send: 0.26 });
    },

    /**
     * One comic panel finishing its reveal. Distinct from `phase`, which narrates the pipeline:
     * this is the sound of a panel appearing, so it is short, dry and panned to where the panel
     * actually is in the strip.
     */
    panelReveal(index, total) {
        if (!canPlay() || throttled(`panel${index}`, 90)) return;

        const span = Math.max(1, total - 1);
        const pan = total > 1 ? ((index / span) * 2 - 1) * 0.7 : 0;

        // A quick swept blip, like ink hitting paper and stopping. The sweep is downward so four
        // of them in sequence do not read as an ascending scale — the panels are siblings, not
        // steps, and an ascending figure would imply the last one matters most.
        voice({
            type: 'triangle', startFreq: 880 + index * 40, endFreq: 420,
            attack: 0.002, decay: 0.13, peak: 0.20, pan, send: 0.16
        });
        noise({ duration: 0.09, peak: 0.10, filterHz: 2400, delay: 0.005, pan, send: 0.18 });
    },

    /**
     * A comic's own motif, derived from its place id and its score.
     *
     * WHY THIS IS NOT DECORATION. Every restaurant now sounds like itself, deterministically and
     * forever: the same place always plays the same figure, so the Hall of Fame becomes something
     * you can recognise by ear. Nothing else in the app gives a place an identity that is not its
     * name.
     *
     * The seed picks the notes and the contour; the SCORE picks the scale, the tempo and the
     * timbre — so two restaurants sound different from each other, and a strange one sounds
     * strange rather than merely different. Zero assets: it is the same `voice()` primitive
     * everything else uses, driven by a seeded PRNG.
     */
    signature(seed, score = 50) {
        if (!canPlay() || throttled('signature', 600)) return;
        motif(seed, score);
    },

    // ── Moment cues, composed in fx.js ───────────────────────────────────────────────────

    /**
     * "Too normal to draw." A deflating sad-trombone: three falling sawtooth notes, the last one
     * sagging off pitch with a wobble. Funny on purpose — the place was rejected for being boring,
     * which is a joke, not a failure.
     */
    fizzle() {
        if (!canPlay() || throttled('fizzle', 900)) return;
        const notes = [293.66, 277.18, 261.63];
        notes.forEach((freq, i) => voice({
            type: 'sawtooth', freq, attack: 0.02, decay: 0.22, peak: 0.09, delay: i * 0.26, pan: 0, send: 0.12
        }));
        const ctx = state.ctx;
        const t0 = ctx.currentTime + notes.length * 0.26;
        const osc = ctx.createOscillator();
        osc.type = 'sawtooth';
        osc.frequency.setValueAtTime(246.94, t0);
        osc.frequency.exponentialRampToValueAtTime(196, t0 + 0.9);
        const wobble = ctx.createOscillator();
        wobble.frequency.value = 6;
        const depth = ctx.createGain();
        depth.gain.value = 9;
        wobble.connect(depth);
        depth.connect(osc.frequency);
        const filter = ctx.createBiquadFilter();
        filter.type = 'lowpass';
        filter.frequency.value = 1100;
        const gain = ctx.createGain();
        gain.gain.setValueAtTime(0.0001, t0);
        gain.gain.exponentialRampToValueAtTime(0.1, t0 + 0.04);
        gain.gain.exponentialRampToValueAtTime(0.0001, t0 + 1.0);
        const output = makeOutput(0, 0.15);
        osc.connect(filter);
        filter.connect(gain);
        gain.connect(output.node);
        osc.start(t0);
        wobble.start(t0);
        osc.stop(t0 + 1.05);
        wobble.stop(t0 + 1.05);
        osc.onended = () => {
            osc.disconnect(); wobble.disconnect(); depth.disconnect();
            filter.disconnect(); gain.disconnect(); output.dispose();
        };
    },

    /** The map flying to a pin: air moving past, panned to where the pin was tapped. */
    whoosh(clientX) {
        if (!canPlay() || throttled('whoosh', 300)) return;
        const ctx = state.ctx;
        const t0 = ctx.currentTime;
        const source = ctx.createBufferSource();
        source.buffer = noiseBuffer(0.7);
        const filter = ctx.createBiquadFilter();
        filter.type = 'bandpass';
        filter.Q.value = 1.4;
        filter.frequency.setValueAtTime(400, t0);
        filter.frequency.exponentialRampToValueAtTime(2600, t0 + 0.3);
        filter.frequency.exponentialRampToValueAtTime(500, t0 + 0.7);
        const gain = ctx.createGain();
        gain.gain.setValueAtTime(0.0001, t0);
        gain.gain.exponentialRampToValueAtTime(0.16, t0 + 0.28);
        gain.gain.exponentialRampToValueAtTime(0.0001, t0 + 0.7);
        const output = makeOutput(panForClientX(clientX ?? window.innerWidth / 2), 0.3);
        source.connect(filter);
        filter.connect(gain);
        gain.connect(output.node);
        source.start(t0);
        source.onended = () => { source.disconnect(); filter.disconnect(); gain.disconnect(); output.dispose(); };
    },

    /** The rubber stamp landing: a wooden knock over a dull body thud. */
    pressClunk() {
        if (!canPlay() || throttled('pressClunk', 500)) return;
        voice({ type: 'sine', startFreq: 160, endFreq: 55, attack: 0.002, decay: 0.22, peak: 0.55, send: 0.12 });
        voice({ type: 'triangle', startFreq: 720, endFreq: 380, attack: 0.001, decay: 0.07, peak: 0.24, send: 0.1 });
        noise({ duration: 0.12, peak: 0.22, filterHz: 1400, send: 0.15 });
    },

    /** A new number one on the board: a quick brass-ish fanfare, rising, panned to the row. */
    crown(clientX) {
        if (!canPlay() || throttled('crown', 1500)) return;
        const pan = panForClientX(clientX ?? window.innerWidth / 2);
        const figure = [392.0, 523.25, 659.25, 783.99, 1046.5];
        figure.forEach((freq, i) => {
            const last = i === figure.length - 1;
            voice({ type: 'sawtooth', freq, attack: 0.008, decay: last ? 0.7 : 0.11, peak: last ? 0.14 : 0.1, delay: i * 0.085, pan, send: 0.35 });
            voice({ type: 'triangle', freq: freq * 2, attack: 0.008, decay: last ? 0.6 : 0.09, peak: 0.05, delay: i * 0.085, pan: -pan * 0.5, send: 0.4 });
        });
        noise({ duration: 0.9, peak: 0.05, filterHz: 7000, filterType: 'highpass', delay: 0.34, pan, send: 0.6 });
    },

    /** One pipeline phase reached: adds its voice to the wait bed and plays its texture. */
    waitPhase(index, total) {
        if (!canPlay()) return;
        if (!wait) {
            const ctx = state.ctx;
            const filter = ctx.createBiquadFilter();
            filter.type = 'lowpass';
            filter.frequency.value = 280;
            const gain = ctx.createGain();
            gain.gain.value = 0.16;
            const output = makeOutput(0, 0.5);
            filter.connect(gain);
            gain.connect(output.node);
            wait = { filter, gain, output, nodes: [], oscillators: [], voices: 0, timer: 0 };
            wait.timer = setTimeout(() => endWaitBed('abandon'), WAIT_MAX_MS);
        }
        // A skipped phase still adds its voice, so the chord always matches the progress.
        const target = Math.min(Math.max(0, index) + 1, WAIT_CHORD.length);
        while (wait.voices < target) addWaitVoice(wait.voices++);
        const progress = (index + 1) / Math.max(1, total ?? WAIT_CHORD.length);
        wait.filter.frequency.setTargetAtTime(280 + progress * 1500, state.ctx.currentTime, 0.4);
        if (!throttled(`wait${index}`, 250)) waitTexture(index);
    },

    /** 'done' lets the bed go under the score roll, 'fail' sags it, 'abandon' cuts it short. */
    endWait(outcome) {
        endWaitBed(outcome);
    },

    /**
     * A continuous voice for the sprung score needle: pitch follows where the needle is, loudness
     * follows how fast it moves, so the overshoot and wobble are heard as well as seen. Null when
     * nothing can sound. update(position 0..1, speed 0..1); stop() lets it go.
     */
    needle() {
        if (!canPlay()) return null;
        const ctx = state.ctx;
        const osc = ctx.createOscillator();
        osc.type = 'triangle';
        osc.frequency.value = 160;
        const filter = ctx.createBiquadFilter();
        filter.type = 'lowpass';
        filter.frequency.value = 1400;
        const gain = ctx.createGain();
        gain.gain.value = 0.0001;
        const output = makeOutput(0, 0.22);
        osc.connect(filter);
        filter.connect(gain);
        gain.connect(output.node);
        osc.start();
        let stopped = false;
        return {
            update(position, speed) {
                if (stopped) return;
                const t = ctx.currentTime;
                osc.frequency.setTargetAtTime(160 + Math.max(0, position) * 540, t, 0.012);
                gain.gain.setTargetAtTime(Math.max(0.0001, Math.min(1, speed) * 0.16), t, 0.02);
                if (output.node.pan) output.node.pan.setTargetAtTime((position - 0.5) * 0.8, t, 0.03);
            },
            stop() {
                if (stopped) return;
                stopped = true;
                release(gain, [osc, filter, gain], 0.12);
                setTimeout(() => output.dispose(), 300);
            }
        };
    },

    /**
     * A map pin landing: a pluck panned to where it fell. Pitch climbs with the order the pins
     * land in; a drawn (scored) pin rings brighter than an undrawn one.
     */
    pinDrop(clientX, rank, score) {
        if (!canPlay() || throttled(`pin${rank}`, 80)) return;
        const pan = panForClientX(clientX);
        const scored = typeof score === 'number';
        const freq = PENTATONIC[rank % PENTATONIC.length] * (rank >= PENTATONIC.length ? 2 : 1) / 2;
        voice({ type: scored ? 'triangle' : 'sine', freq, attack: 0.002, decay: scored ? 0.22 : 0.12, peak: scored ? 0.14 : 0.08, pan, send: 0.2 });
        noise({ duration: 0.035, peak: 0.08, filterHz: 700, pan, send: 0.05 });
    },

    /** Ink thrown off the rubber stamp: a wet splat, then droplets pattering down around it. */
    inkSplash() {
        if (!canPlay() || throttled('inkSplash', 500)) return;
        noise({ duration: 0.16, peak: 0.16, filterHz: 900, send: 0.15 });
        let at = 0.04;
        for (let i = 0; i < 7; i++) {
            at += 0.03 + Math.random() * 0.06;
            noise({ duration: 0.02 + Math.random() * 0.03, peak: 0.05 + Math.random() * 0.05,
                filterHz: 2400 + Math.random() * 2600, filterType: 'bandpass',
                delay: at, pan: (Math.random() - 0.5) * 1.4, send: 0.25 });
        }
    },

    // ── Map sounds (opt-in, `posee_map_sounds`) ──────────────────────────────────────────

    mapSoundsEnabled: () => state.mapSounds,

    setMapSounds(enabled) {
        state.mapSounds = !!enabled;
        try {
            localStorage.setItem(MAP_SOUNDS_KEY, String(state.mapSounds));
        } catch {
            // Session-only preference.
        }
        if (!state.mapSounds) this.mapHumStop();
        return state.mapSounds;
    },

    /**
     * Air past the camera while the user drags or zooms: a looped noise source whose level and
     * brightness follow the speed (CSS px per second). Falls silent on its own when the updates
     * stop, and is torn down after a couple of idle seconds.
     */
    mapWind(speed) {
        if (!mapAudible()) return;
        const ctx = state.ctx;
        if (!wind) {
            const source = ctx.createBufferSource();
            source.buffer = noiseBuffer(2);
            source.loop = true;
            const filter = ctx.createBiquadFilter();
            filter.type = 'bandpass';
            filter.Q.value = 0.9;
            const gain = ctx.createGain();
            gain.gain.value = 0.0001;
            const output = makeOutput(0, 0.25);
            source.connect(filter);
            filter.connect(gain);
            gain.connect(output.node);
            source.start();
            wind = { source, filter, gain, output, idle: 0 };
        }
        const t = ctx.currentTime;
        const pace = Math.min(1, Math.max(0, speed) / 2500);
        wind.filter.frequency.setTargetAtTime(300 + pace * 2200, t, 0.05);
        wind.gain.gain.setTargetAtTime(Math.max(0.0001, pace * 0.14), t, 0.04);
        wind.gain.gain.setTargetAtTime(0.0001, t + 0.12, 0.15);
        clearTimeout(wind.idle);
        wind.idle = setTimeout(() => {
            const w = wind;
            wind = null;
            if (!w) return;
            release(w.gain, [w.source, w.filter, w.gain], 0.1);
            setTimeout(() => w.output.dispose(), 300);
        }, 2000);
    },

    /** "Search this area" offering itself: a low sonar ping with a long, detuned return. */
    mapPing() {
        if (!mapAudible() || throttled('mapPing', 1200)) return;
        voice({ type: 'sine', freq: 659.25, attack: 0.003, decay: 0.22, peak: 0.2, send: 0.5 });
        voice({ type: 'sine', freq: 659.25, attack: 0.005, decay: 0.7, peak: 0.06, delay: 0.34, detune: -18, send: 0.8 });
    },

    /**
     * The heat under the pins, heard: two detuned low sines whose pitch rises with the drawn
     * places' mean strangeness and whose level rises with how many there are. Barely there.
     */
    mapHum(count, meanScore) {
        if (!mapAudible() || !(count > 0)) {
            this.mapHumStop();
            return;
        }
        const ctx = state.ctx;
        const t = ctx.currentTime;
        const freq = 55 * Math.pow(SEMITONE, Math.min(100, Math.max(0, meanScore)) / 100 * 12);
        const level = Math.min(0.09, 0.03 + count * 0.004);
        if (!hum) {
            const gain = ctx.createGain();
            gain.gain.setValueAtTime(0.0001, t);
            const filter = ctx.createBiquadFilter();
            filter.type = 'lowpass';
            filter.frequency.value = 420;
            const output = makeOutput(0, 0.4);
            const a = ctx.createOscillator();
            const b = ctx.createOscillator();
            b.detune.value = 9;
            const lfo = ctx.createOscillator();
            lfo.frequency.value = 0.18;
            const depth = ctx.createGain();
            depth.gain.value = 0.35;
            lfo.connect(depth);
            const swell = ctx.createGain();
            swell.gain.value = 1;
            depth.connect(swell.gain);
            a.connect(swell);
            b.connect(swell);
            swell.connect(filter);
            filter.connect(gain);
            gain.connect(output.node);
            a.start(); b.start(); lfo.start();
            hum = { a, b, gain, output, nodes: [a, b, lfo, depth, swell, filter, gain] };
        }
        hum.a.frequency.setTargetAtTime(freq, t, 0.4);
        hum.b.frequency.setTargetAtTime(freq * 1.5, t, 0.4);
        hum.gain.gain.setTargetAtTime(level, t, 0.6);
    },

    mapHumStop() {
        if (!hum || !state.ctx) return;
        const h = hum;
        hum = null;
        release(h.gain, h.nodes, 0.8);
        setTimeout(() => h.output.dispose(), 1000);
    },

    /**
     * The output stage for a long-running voice that lives outside this file (the comic
     * soundtrack). Null until the context is running: such a voice has to wait for a gesture
     * like everything else.
     */
    bus() {
        if (!canPlay()) return null;
        return { ctx: state.ctx, makeOutput };
    }
};
