// Single entry point for everything in js/. Loaded as a module from index.html; publishes one
// flat `window.poseeFx` surface because that is how the existing interop in this app works
// (window.geolocation, window.shareUtils) and mixing two conventions helps nobody.
//
// Every method here is defensive. Blazor calls these from component lifecycle methods, and a
// throw inside JS interop surfaces to the user as the framework's red error strip — which for
// decoration is a spectacularly bad trade. Nothing in this file may ever throw into .NET.
//
// THIS FILE IS ALSO THE COMPOSITION ROOT for the effects that pair with each other. audio.js
// does not know haptics exist and comic-reveal.js does not know the app makes noise. Deciding
// that a tap should also buzz is a product decision and it is made here.

import { gfx } from './gfx-core.js';
import { audio } from './audio.js';
import { haptics } from './haptics.js';
import * as gradient from './gradient.js';
import * as panelScrub from './panel-scrub.js';
import * as comicReveal from './comic-reveal.js';
import * as viewTransitions from './view-transitions.js';
import * as confetti from './confetti.js';
import * as glass from './glass.js';
import * as motes from './ink-motes.js';
import { soundtrack } from './soundtrack.js';

function guard(fn, fallback = null) {
    try {
        return fn();
    } catch (err) {
        console.warn('[fx] call failed', err);
        return fallback;
    }
}

async function guardAsync(fn, fallback = null) {
    try {
        return await fn();
    } catch (err) {
        console.warn('[fx] async call failed', err);
        return fallback;
    }
}

const info = gfx.init();
const audioInfo = audio.init();
haptics.init(audioInfo.enabled);

viewTransitions.init({
    onNavigate: (direction, clientX) => guard(() => audio.navigate(direction, clientX))
});
guard(() => glass.init());

function propagateAudioState(enabled) {
    guard(() => haptics.syncAudio(enabled));
    if (!enabled) {
        guard(() => haptics.cancel());
        guard(() => soundtrack.halt());
    } else {
        // A gesture that brought sound back may be the first moment the wanted loop can play.
        guard(() => soundtrack.resume(audio.bus()));
    }
}

function reflectTier(tier) {
    guard(() => {
        document.documentElement.dataset.fxTier = tier;
    });
}
reflectTier(info.tier);
gfx.onTierChanged(reflectTier);

/** Heard and felt when sound or haptics is switched ON: proof the switch did something. */
function toggleTick() {
    audio.tapAt(window.innerWidth / 2);
    haptics.tap();
}

// CERTIFIED STRANGE: the rubber stamp slammed onto the comic after a share or save. DOM + CSS
// (`.fx-stamp` in app.css), so it needs no GPU; the slam and fade are one CSS animation.
const STAMP_IMPACT_MS = 150;
function stampComic(container) {
    const host = container.parentElement;
    if (!host) return false;
    if (getComputedStyle(host).position === 'static') host.style.position = 'relative';
    const el = document.createElement('div');
    el.className = 'fx-stamp';
    el.setAttribute('aria-hidden', 'true');
    el.innerHTML = '<span>Certified</span><span>Strange</span>';
    // Layout offsets, not client rects: the tilt wrapper carries a scroll-driven transform.
    el.style.left = `${container.offsetLeft + container.offsetWidth / 2}px`;
    el.style.top = `${container.offsetTop + container.offsetHeight / 2}px`;
    el.style.setProperty('--fx-stamp-w', `${container.offsetWidth}px`);
    el.addEventListener('animationend', () => el.remove(), { once: true });
    setTimeout(() => el.remove(), 3000);
    host.appendChild(el);
    setTimeout(() => guard(() => {
        // Ink thrown off the impact, from wherever the comic is on screen by then.
        const rect = container.getBoundingClientRect();
        confetti.splat(rect.left + rect.width / 2, rect.top + rect.height / 2, { spread: Math.max(0.6, rect.width / 640) });
        audio.pressClunk();
        audio.inkSplash();
        haptics.confirm();
    }), STAMP_IMPACT_MS);
    return true;
}

// The score needle: a damped spring instead of a CSS ease. Stranger places are less damped, so
// they overshoot harder and wobble longer — a 30 settles almost at once, a 92 swings past the
// top, dips and rings. Peak time is the roll's duration, so the landing chord (scheduled on
// the same figure) lands on the overshoot. The ring is found by class: the page renders it in
// the same pass that asks for the roll, so it is waited for a few frames.
const RING_SELECTOR = '.strangeness-ring-wrap';

function springScore(score, durationMs) {
    const target = Math.min(100, Math.max(0, score));
    const zeta = 0.7 - (target / 100) * 0.35;
    const omegaD = Math.PI / (durationMs / 1000);
    const omegaN = omegaD / Math.sqrt(1 - zeta * zeta);
    const decay = zeta * omegaN;
    const settleS = Math.log(200) / decay;
    const sine = zeta / Math.sqrt(1 - zeta * zeta);
    const response = (t) => 1 - Math.exp(-decay * t) * (Math.cos(omegaD * t) + sine * Math.sin(omegaD * t));

    let ring = null;
    let waited = 0;
    let t0 = 0;
    let previous = 0;
    let needle = null;
    const stop = gfx.addTask('score-spring', (now, elapsed) => {
        if (!ring) {
            ring = document.querySelector(RING_SELECTOR);
            if (!ring) {
                if (++waited > 30) stop();
                return;
            }
            ring.dataset.scoreSpring = '';
            t0 = now;
            needle = audio.needle();
        }
        const t = (now - t0) / 1000;
        if (!ring.isConnected || t >= settleS) {
            ring.style.removeProperty('--score');
            delete ring.dataset.scoreSpring;
            needle?.stop();
            stop();
            return;
        }
        const value = target * response(t);
        ring.style.setProperty('--score', String(Math.round(Math.min(100, Math.max(0, value)))));
        const speed = Math.abs(value - previous) / Math.max(1e-3, (elapsed || 16.7) / 1000);
        previous = value;
        needle?.update(value / 100, speed / Math.max(1, target * omegaN));
    });
}

// At most this many rows of a reshuffled board make a sound; the rest only move.
const MAX_VOICED_MOVES = 4;

export const fx = {
    // ── Capability + tier ────────────────────────────────────────────────────────────────
    describe: () => guard(() => ({
        ...gfx.describe(),
        haptics: haptics.describe().supported
    }), {
        tier: 'off', reducedMotion: true, webgl2: false, autoDowngraded: false,
        haptics: false
    }),
    setTier: (tier) => guard(() => gfx.setTier(tier), 'off'),
    stats: () => guard(() => gfx.stats(), null),
    pageVisible: () => guard(() => document.visibilityState === 'visible', false),

    // ── Audio ────────────────────────────────────────────────────────────────────────────
    audioEnabled: () => guard(() => audio.isEnabled(), false),

    setAudioEnabled: (enabled) => guardAsync(async () => {
        const applied = await audio.setEnabled(enabled);
        propagateAudioState(applied);
        if (applied) toggleTick();
        return applied;
    }, false),

    volume: () => guard(() => audio.volume(), 1),
    setVolume: (volume) => guard(() => audio.setVolume(volume), 1),

    /** Call from a real click handler or the AudioContext will not leave 'suspended'. */
    unlockAudio: () => guardAsync(async () => {
        const unlocked = await audio.unlock();
        propagateAudioState(unlocked && audio.isEnabled());
        return unlocked;
    }, false),

    /** Pans the click to a viewport x coordinate — see audio.tapAt. */
    playTapAt: (clientX) => guard(() => {
        audio.tapAt(clientX);
        haptics.tap();
    }),
    /**
     * The discovery beats. These exist because the landing page — the first screen every visitor
     * sees — had exactly two cues on it: a tap, and the audio unlock hung off the same handler.
     * Asking for your location, waiting, and getting fifteen restaurants back all sounded
     * identical to pressing a button.
     */
    playLocating: () => guard(() => {
        audio.locating();
        haptics.tap();
    }),
    playArrival: (count) => guard(() => {
        audio.arrival(count ?? 0);
        haptics.confirm();
    }),
    /** Zero results. Not an error cue: an empty answer is an answer. */
    playEmpty: () => guard(() => audio.empty()),
    /** "Too normal to draw": a deflating sad trombone. A joke about the place, not an error. */
    playFizzle: () => guard(() => audio.fizzle()),
    /** The map flying to a tapped pin; called by map-view.js through this surface. */
    playMapFly: (clientX) => guard(() => audio.whoosh(clientX)),

    /**
     * Tap on a card, voiced by whether the comic already exists.
     *
     * A cache hit opens instantly and costs nothing; a miss spends a paid image call and about
     * ten seconds. Same control, wildly different consequence, and until now the same click.
     */
    playTapCached: (clientX, cached) => guard(() => {
        if (cached) {
            audio.tapCached(clientX);
            haptics.tap();
        } else {
            audio.tapUncached(clientX);
            haptics.tap();
        }
    }),

    /**
     * The whole count-up and its landing chord in one call, timed by the audio clock. From `lite`
     * up the needle is sprung and voices itself instead of ticking; the landing ripples the backdrop.
     */
    playScoreRoll: (score, durationMs) => guard(() => {
        const sprung = durationMs > 0 && gfx.allows('lite');
        if (sprung) springScore(score, durationMs);
        audio.scoreRoll(score, durationMs, { ticks: !sprung });
        setTimeout(() => guard(() => {
            haptics.confirm();
            gradient.kick(score);
        }), Math.max(0, durationMs ?? 0));
    }),

    // ── The generation wait ──────────────────────────────────────────────────────────────
    /** A pipeline phase reached: one more voice in the wait bed, its texture, a light tap. */
    playPhase: (index, total) => guard(() => {
        audio.waitPhase(index, total);
        haptics.tap();
    }),
    /** 'done' (the score roll resolves it), 'fail' (it sags) or 'abandon'. */
    endWait: (outcome) => guard(() => audio.endWait(outcome)),

    /** A map pin landing; called by map-view.js through this surface. */
    playPinDrop: (clientX, rank, score) => guard(() => audio.pinDrop(clientX, rank, score)),

    // ── Map sounds (opt-in) ──────────────────────────────────────────────────────────────
    mapSoundsEnabled: () => guard(() => audio.mapSoundsEnabled(), false),
    /** From the settings page's click, so the context can unlock on the same gesture. */
    setMapSoundsEnabled: (enabled) => guardAsync(async () => {
        const applied = audio.setMapSounds(enabled);
        if (applied) {
            await audio.unlock();
            audio.mapPing();
        }
        return applied;
    }, false),
    mapWind: (speed) => guard(() => audio.mapWind(speed)),
    mapHum: (count, meanScore) => guard(() => audio.mapHum(count, meanScore)),
    mapHumStop: () => guard(() => audio.mapHumStop()),
    playMapPing: () => guard(() => audio.mapPing()),
    playShareStinger: () => guard(() => {
        audio.shareStinger();
        haptics.confirm();
    }),
    playError: () => guard(() => {
        audio.error();
        haptics.error();
    }),
    playConfirm: () => guard(() => {
        audio.confirm();
        haptics.confirm();
    }),
    /**
     * The comic's own motif, seeded from its place id. Deterministic: the same restaurant always
     * plays the same figure, which is what makes it an identity rather than a flourish.
     */
    playSignature: (seed, score) => guard(() => audio.signature(seed, score)),

    // ── Soundtrack (opt-in) ──────────────────────────────────────────────────────────────
    soundtrackEnabled: () => guard(() => soundtrack.isEnabled(), false),
    /** From the settings page's click, so the context can unlock on the same gesture. */
    setSoundtrackEnabled: (enabled) => guardAsync(async () => {
        const applied = soundtrack.setEnabled(enabled);
        if (applied) {
            await audio.unlock();
            soundtrack.resume(audio.bus());
        }
        return applied;
    }, false),
    /** The comic page asks for its loop; it starts now if it can, or when sound next unlocks. */
    startSoundtrack: (seed, score) => guard(() => soundtrack.want(audio.bus(), seed, score)),
    stopSoundtrack: () => guard(() => soundtrack.release()),

    // ── Haptics ──────────────────────────────────────────────────────────────────────────
    hapticsDescribe: () => guard(() => haptics.describe(), { supported: false, enabled: false, explicit: false }),
    setHapticsEnabled: (enabled) => guard(() => {
        const active = haptics.setEnabled(enabled);
        if (active) toggleTick();
        return active;
    }, false),

    // ── Background gradient ──────────────────────────────────────────────────────────────
    // The backdrop's audio meter is handed over here: gradient.js does not import audio.js.
    startGradient: (canvas, score) => guard(() => gradient.start(canvas, { score, level: audio.level }), 0),
    setGradientScore: (id, score) => guard(() => gradient.setScore(id, score)),
    stopGradient: (id) => guard(() => gradient.stop(id)),

    // ── Ink motes (WebGPU, opt-in) ───────────────────────────────────────────────────────
    motesDescribe: () => guard(() => motes.describe(), { supported: false, enabled: false, explicit: false }),
    setMotesEnabled: (enabled) => guard(() => motes.setEnabled(enabled), false),
    startMotes: (score) => guard(() => motes.start(score), 0),
    stopMotes: (id) => guard(() => motes.stop(id)),

    // ── Ink development ──────────────────────────────────────────────────────────────────

    /**
     * Develops the comic onto the page top-down through a CSS mask. Each band gets a cue as it
     * becomes recognisable — composed here, since comic-reveal.js does not know the app makes noise.
     */
    startComicReveal: (container, bands) => guard(() => comicReveal.start(container, {
        bands: bands ?? 2,
        onBand: (index, total) => {
            audio.panelReveal(index, total);
            haptics.tap();
        }
    }), 0),

    finishComicReveal: (id) => guard(() => comicReveal.finish(id)),

    // ── Reading the strip ────────────────────────────────────────────────────────────────

    /**
     * Plays the comic as it is read: one note of its own motif per panel, as that panel crosses
     * the middle of the screen.
     *
     * The note choice is composed here, not in panel-scrub.js — that module knows only that a
     * boundary was crossed. Deliberately NOT the same cue as the reveal's `panelReveal`: this is
     * the comic's identity, re-heard at the reader's own pace, so it is the signature's own
     * scale rather than a generic blip. `seed` is the place id for the same reason it always is.
     */
    startPanelScrub: (container, panels, seed, score) => guard(() => panelScrub.start(container, {
        panels: panels ?? 2,
        onPanel: (index, total) => {
            audio.panelNote(seed, score ?? 50, index, total);
        }
    }), 0),

    stopPanelScrub: (id) => guard(() => panelScrub.stop(id)),

    // ── Leaderboard ──────────────────────────────────────────────────────────────────────

    /**
     * Slides each row that moved since last visit from its old slot to its new one, then voices
     * the move where it happened. Rows are measured, not assumed: a stacked mobile row is three
     * times the height of a desktop one. Climbers ride above fallers so the winner is never
     * drawn underneath the row it passed.
     */
    animateBoardMoves: (container) => guard(() => {
        if (!container || !gfx.allows('lite')) return;
        const rows = [...container.querySelectorAll('[data-delta]')];
        const bodies = [];
        let voiced = 0;
        rows.forEach((row, order) => {
            const delta = Number(row.dataset.delta);
            if (!delta) return;
            const rect = row.getBoundingClientRect();
            const delay = 250 + order * 70;
            row.style.zIndex = delta > 0 ? '2' : '1';
            // Each mover is a damped spring pulled from its old slot to its new one. A climber
            // is launched with an extra upward kick so it overshoots its new rank and drops back
            // in; a faller is a heavier body that lands short, bounces, and wobbles.
            bodies.push({
                row, delta, delay,
                y: delta * rect.height,
                v: delta > 0 ? -Math.min(6, delta) * 260 : 0,
                stiffness: delta > 0 ? 210 : 260,
                damping: delta > 0 ? 15 : 11,
                started: false,
                landed: false,
                clientX: rect.left + rect.width / 2,
                voice: voiced++ < MAX_VOICED_MOVES
            });
            row.style.transform = `translateY(${delta * rect.height}px)`;
        });
        if (bodies.length === 0) return;

        // A climb into first place is a coronation: confetti from the row, and a fanfare.
        const champion = rows[0];
        const crowned = Number(champion?.dataset.delta) > 0;

        const t0 = performance.now();
        let last = t0;
        const stop = gfx.addTask('board-springs', (now) => {
            const dt = Math.min(1 / 30, (now - last) / 1000);
            last = now;
            let moving = 0;
            for (const body of bodies) {
                if (now - t0 < body.delay) {
                    moving++;
                    continue;
                }
                if (!body.row.isConnected) continue;
                const force = -body.stiffness * body.y - body.damping * body.v;
                body.v += force * dt;
                body.y += body.v * dt;

                // The cue and the buzz land as the row first reaches its slot, not as it sets
                // off: the arrival is the news. The staggered delays keep movers from stacking.
                const arrived = body.delta > 0 ? body.y <= 0 : body.y >= 0;
                if (arrived && !body.landed) {
                    body.landed = true;
                    if (body.voice) audio.rankMove(body.delta, body.clientX, 0);
                    if (Math.abs(body.delta) >= 3) haptics.tap();
                    if (crowned && body.row === champion) {
                        confetti.burst(champion);
                        audio.crown(body.clientX);
                        haptics.confirm();
                    }
                }

                const settled = Math.abs(body.y) < 0.4 && Math.abs(body.v) < 6;
                if (settled) {
                    body.row.style.transform = '';
                    body.row.style.zIndex = '';
                    continue;
                }
                moving++;
                // A little lean in the direction of travel sells the mass.
                const lean = Math.max(-2.5, Math.min(2.5, body.v * 0.0035));
                const lift = body.delta > 0 ? 1 + Math.min(0.03, Math.abs(body.v) * 0.00002) : 1;
                body.row.style.transform = `translateY(${body.y.toFixed(2)}px) rotate(${lean.toFixed(3)}deg) scale(${lift.toFixed(4)})`;
            }
            if (moving === 0 || now - t0 > 6000) {
                for (const body of bodies) {
                    body.row.style.transform = '';
                    body.row.style.zIndex = '';
                }
                stop();
            }
        });
    }),

    // ── Hot off the press ────────────────────────────────────────────────────────────────

    /** After a share or save, CERTIFIED STRANGE lands on the comic. Lite tier and up. */
    stampComic: () => guard(() => {
        const container = document.querySelector('.comic-strip-container');
        return !!container && gfx.allows('lite') && stampComic(container);
    }, false),

    // ── Route transitions ────────────────────────────────────────────────────────────────
    /** Called after the destination route renders, to close the open transition. */
    settleViewTransition: () => guard(() => viewTransitions.settle())

};

window.poseeFx = fx;
