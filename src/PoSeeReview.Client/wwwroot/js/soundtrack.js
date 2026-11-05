// A comic's own soundtrack: a four-bar loop generated from its place id and its score.
//
// THE MOMENT THIS IS FOR. The signature motif plays once as the score lands and is gone; a
// reader who stays with the comic — reading the narrative, scrolling the panels, listening to
// it read aloud — sits in silence. This gives the place a room to sit in. Same seed, same loop,
// forever: a restaurant you return to sounds like it did last time.
//
// ON BY DEFAULT (the owner's call), like every other sound: it only plays once sound is unlocked
// by a tap. The switch sits in /settings next to sound and haptics, stored as `posee_soundtrack`.
//
// What the score does (the seed only ever picks notes):
//   * scale     — the same SCALES ladder the motif uses, so the loop and the motif agree
//   * tempo     — 70bpm at 0, ~124 at 100
//   * texture   — hats come in above 40; above 70 the bus is driven through a waveshaper and a
//                 staircase curve (a bitcrusher without an AudioWorklet), and the pad detunes
//   * the room  — the reverb send rises with it, like the landing chord's
//
// Scheduling is the standard look-ahead pattern: a coarse timer queues the next ~300ms of notes
// on the audio clock, which is what keeps timing tight whatever the main thread is doing. It is
// a timer, not an animation frame, so it does not belong on gfx-core's rAF loop. A hidden tab
// fades the loop out rather than letting a throttled timer stutter it.

import { hashString, seededRandom, SCALES, SEMITONE } from './audio.js';

const STORAGE_KEY = 'posee_soundtrack';
const LOOKAHEAD_S = 0.3;
const TICK_MS = 90;
const LEVEL = 0.55;

function readEnabled() {
    try {
        return localStorage.getItem(STORAGE_KEY) !== 'false';   // on unless switched off
    } catch {
        return true;
    }
}

/**
 * The whole loop, decided up front from the seed: four bars of chord roots, and a sixteen-step
 * arpeggio pattern per bar. Deciding it once is what makes it a loop rather than a ramble.
 */
function compose(seed, score) {
    const strange = Math.min(1, Math.max(0, score / 100));
    const random = seededRandom(hashString(`${seed}:soundtrack`));
    const scale = SCALES[Math.min(SCALES.length - 1, Math.floor(strange * SCALES.length))];
    const root = 110 * Math.pow(SEMITONE, Math.floor(random() * 8) - 3);
    const degreeFreq = (degree, octave = 0) => {
        const steps = scale.length;
        const wrapped = ((degree % steps) + steps) % steps;
        const octaves = Math.floor(degree / steps) + octave;
        return root * Math.pow(SEMITONE, scale[wrapped] + 12 * octaves);
    };

    const bars = [0, 0, 0, 0].map((_, bar) => {
        const chord = bar === 0 ? 0 : Math.floor(random() * scale.length);
        const steps = Array.from({ length: 16 }, (_, step) => {
            const density = 0.35 + strange * 0.35;
            const on = step % 4 === 0 || random() < density;
            return on ? chord + [0, 2, 4, 7][Math.floor(random() * 4)] : null;
        });
        return { chord, steps };
    });

    return {
        strange,
        bars,
        degreeFreq,
        stepSeconds: 60 / (70 + strange * 54) / 4,
        type: strange < 0.4 ? 'triangle' : 'sawtooth'
    };
}

/** Staircase transfer curve: amplitude quantised to 2^bits levels, with a soft drive before it. */
function gritCurve(strange) {
    const curve = new Float32Array(2048);
    const levels = strange > 0.9 ? 12 : strange > 0.7 ? 28 : 0;
    const drive = 1 + Math.max(0, strange - 0.5) * 4;
    const norm = Math.tanh(drive);
    for (let i = 0; i < curve.length; i++) {
        const x = (i / (curve.length - 1)) * 2 - 1;
        const driven = Math.tanh(x * drive) / norm;
        curve[i] = levels ? Math.round(driven * levels) / levels : driven;
    }
    return curve;
}

const state = {
    enabled: readEnabled(),
    wanted: null,       // { seed, score } the page asked for, playable or not
    playing: null       // the running loop
};

function note(loop, { freq, at, length, peak, type, pan = 0, attack = 0.01 }) {
    const { ctx } = loop;
    const osc = ctx.createOscillator();
    osc.type = type;
    osc.frequency.setValueAtTime(freq, at);
    const gain = ctx.createGain();
    gain.gain.setValueAtTime(0.0001, at);
    gain.gain.exponentialRampToValueAtTime(peak, at + attack);
    gain.gain.exponentialRampToValueAtTime(0.0001, at + length);
    const panner = ctx.createStereoPanner ? ctx.createStereoPanner() : null;
    if (panner) panner.pan.value = pan;
    osc.connect(gain);
    (panner ? gain.connect(panner) : gain).connect(loop.input);
    osc.onended = () => { osc.disconnect(); gain.disconnect(); panner?.disconnect(); };
    osc.start(at);
    osc.stop(at + length + 0.05);
}

function hat(loop, at, peak) {
    const { ctx } = loop;
    const length = 0.04;
    const buffer = ctx.createBuffer(1, Math.floor(ctx.sampleRate * length), ctx.sampleRate);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
    const source = ctx.createBufferSource();
    source.buffer = buffer;
    const filter = ctx.createBiquadFilter();
    filter.type = 'highpass';
    filter.frequency.value = 7000;
    const gain = ctx.createGain();
    gain.gain.setValueAtTime(peak, at);
    gain.gain.exponentialRampToValueAtTime(0.0001, at + length);
    source.connect(filter);
    filter.connect(gain);
    gain.connect(loop.input);
    source.start(at);
    source.onended = () => { source.disconnect(); filter.disconnect(); gain.disconnect(); };
}

/** Queues one sixteenth-note step of the loop on the audio clock. */
function scheduleStep(loop, at) {
    const { music } = loop;
    const barIndex = Math.floor(loop.step / 16) % music.bars.length;
    const stepInBar = loop.step % 16;
    const bar = music.bars[barIndex];
    const step = music.stepSeconds;

    if (stepInBar === 0) {
        // Pad: the chord's root, third-ish and fifth-ish, held for the bar and slightly detuned.
        const barLength = step * 16;
        [0, 2, 4].forEach((offset, i) => {
            const freq = music.degreeFreq(bar.chord + offset, 1);
            const detune = 1 + (i - 1) * 0.002 * (1 + music.strange * 4);
            note(loop, {
                freq: freq * detune, at, length: barLength * 1.05, attack: barLength * 0.3,
                peak: 0.035, type: 'triangle', pan: (i - 1) * 0.5 });
        });
    }

    if (stepInBar === 0 || stepInBar === 8) {
        note(loop, {
            freq: music.degreeFreq(bar.chord, 0), at, length: step * 6, peak: 0.11, type: 'sine' });
    }

    const degree = bar.steps[stepInBar];
    if (degree !== null) {
        note(loop, {
            freq: music.degreeFreq(degree, 2), at, length: step * 2.2, attack: 0.004,
            peak: 0.045, type: music.type, pan: stepInBar % 2 ? 0.45 : -0.45 });
    }

    if (music.strange > 0.4 && stepInBar % 4 === 2) {
        hat(loop, at, 0.03 + music.strange * 0.03);
    }

    loop.step++;
}

function tick(loop) {
    const horizon = loop.ctx.currentTime + LOOKAHEAD_S;
    while (loop.nextAt < horizon) {
        scheduleStep(loop, loop.nextAt);
        loop.nextAt += loop.music.stepSeconds;
    }
}

function begin(bus, seed, score) {
    const { ctx, makeOutput } = bus;
    const music = compose(seed, score);

    // The loop's own small bus: fader (fades) → grit → tone → the shared output
    // stage, which sends into the app's one reverb — more of it the stranger the comic.
    const fader = ctx.createGain();
    fader.gain.value = 0.0001;
    const grit = ctx.createWaveShaper();
    grit.curve = gritCurve(music.strange);
    const tone = ctx.createBiquadFilter();
    tone.type = 'lowpass';
    tone.frequency.value = 7000 - music.strange * 3800;
    const output = makeOutput(0, 0.3 + music.strange * 0.35);
    fader.connect(grit);
    grit.connect(tone);
    tone.connect(output.node);

    const loop = {
        ctx, music, input: fader, fader, step: 0,
        nextAt: ctx.currentTime + 0.08,
        timer: 0,
        dispose: () => {
            for (const node of [fader, grit, tone]) {
                try { node.disconnect(); } catch { /* gone */ }
            }
            output.dispose();
        }
    };

    fadeTo(loop, LEVEL, 1.2);
    loop.timer = setInterval(() => {
        try {
            tick(loop);
        } catch {
            halt();
        }
    }, TICK_MS);
    tick(loop);
    return loop;
}

function fadeTo(loop, level, seconds) {
    const now = loop.ctx.currentTime;
    loop.fader.gain.cancelScheduledValues(now);
    loop.fader.gain.setValueAtTime(Math.max(0.0001, loop.fader.gain.value), now);
    loop.fader.gain.exponentialRampToValueAtTime(Math.max(0.0001, level), now + seconds);
}

/** Stops the running loop with a short fade. The wanted comic is kept, so it can resume. */
function halt() {
    const loop = state.playing;
    if (!loop) return;
    state.playing = null;
    clearInterval(loop.timer);
    try {
        fadeTo(loop, 0.0001, 0.5);
    } catch {
        // Context gone; dispose below is all that is left.
    }
    setTimeout(() => loop.dispose(), 700);
}

document.addEventListener('visibilitychange', () => {
    const loop = state.playing;
    if (!loop) return;
    try {
        if (document.hidden) {
            clearInterval(loop.timer);
            loop.timer = 0;
            fadeTo(loop, 0.0001, 0.2);
        } else if (!loop.timer) {
            loop.nextAt = loop.ctx.currentTime + 0.08;
            loop.timer = setInterval(() => tick(loop), TICK_MS);
            fadeTo(loop, LEVEL, 0.8);
        }
    } catch {
        halt();
    }
});

export const soundtrack = {
    isEnabled: () => state.enabled,

    setEnabled(enabled) {
        state.enabled = !!enabled;
        try {
            localStorage.setItem(STORAGE_KEY, String(state.enabled));
        } catch {
            // Session-only preference.
        }
        if (!state.enabled) halt();
        return state.enabled;
    },

    /**
     * Asks for this comic's loop. Remembered even when it cannot play yet (switched off, audio
     * locked, muted) so that turning it on later picks it up without the page asking again.
     * `bus` is audio.bus(): null when the audio context is not running.
     */
    want(bus, seed, score) {
        state.wanted = seed ? { seed, score: score ?? 50 } : null;
        halt();
        this.resume(bus);
    },

    /** Starts the wanted loop if everything allows it now. Called after any gesture that might. */
    resume(bus) {
        if (state.playing || !state.enabled || !state.wanted || !bus) return false;
        state.playing = begin(bus, state.wanted.seed, state.wanted.score);
        return true;
    },

    /** The page is leaving the comic: stop and forget it. */
    release() {
        state.wanted = null;
        halt();
    },

    halt
};
