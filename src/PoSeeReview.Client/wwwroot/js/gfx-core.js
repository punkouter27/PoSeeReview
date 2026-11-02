// Shared foundation for every graphics and audio effect in the app.
//
// Three responsibilities, all of them about keeping the frame budget honest:
//
//  1. ONE requestAnimationFrame loop. Every effect registers a callback here instead of
//     starting its own rAF. N independent loops means N wake-ups per frame and no single
//     place that can measure or stop the work.
//  2. A frame-time budget with AUTOMATIC DOWNGRADE. If the rolling frame cost stays over
//     budget, the tier drops itself. "Sustains 60 FPS" is not something you can assert by
//     writing careful shaders — it has to be measured on the device that is actually running.
//  3. An effects TIER, so a cheap phone, a battery-saver user, and someone who asked their OS
//     for reduced motion all get something coherent rather than whatever happens to be cheap.
//
// Tiers:
//   'off'  — no GPU loops, no audio. Static CSS only.
//   'lite' — audio + CSS materials. No persistent GPU loop.
//   'full' — everything, including the WebGL backdrop.

const STORAGE_KEY = 'posee_fx_tier';
const TIERS = ['off', 'lite', 'full'];

// 60 FPS is 16.67ms. Budget the frame at 20ms so an occasional GC pause is not treated as a
// regression, but a genuinely overloaded device still trips the downgrade.
const FRAME_BUDGET_MS = 20;

// Measured in MILLISECONDS of sustained overrun, not frames. A frame count cannot express
// "1.5 seconds": 90 frames is 1.5s only at 60 FPS, and by the time this guard matters the
// frames are slow — at 60ms each, 90 frames is 5.4s. The guard was therefore slowest to fire
// exactly when the device most needed it. Timing the streak makes the delay constant.
const OVER_BUDGET_MS_BEFORE_DOWNGRADE = 1500;

// The most one frame can add to that streak, so it takes at least 15 slow frames in a row.
const HITCH_CAP_MS = 100;

const state = {
    tier: 'off',
    tierWasAutoDowngraded: false,
    reducedMotion: false,
    webgl2: false,
    tasks: new Map(),
    nextTaskId: 1,
    rafHandle: 0,
    lastFrameStart: 0,
    overBudgetStreak: 0,
    stats: {
        fps: 0,
        frameMs: 0,
        worstFrameMs: 0,
        droppedFrames: 0,
        sampledFrames: 0,
        activeTasks: 0,
        // Time this loop spent in effect callbacks, as opposed to wall time between frames.
        // The gap between the two is everything else on the main thread — Blazor renders, GC,
        // layout — which is exactly what you need to know before optimising a shader that was
        // never the problem.
        cpuMs: 0
    },
    // Rolling mean, cheap: no array allocation per frame.
    frameMsAccumulator: 0,
    frameMsCount: 0,
    cpuMsAccumulator: 0,
    lastStatsFlush: 0,
    listeners: new Set()
};

function detectReducedMotion() {
    try {
        return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    } catch {
        return false;
    }
}

function detectWebGl2() {
    try {
        const canvas = document.createElement('canvas');
        return !!canvas.getContext('webgl2');
    } catch {
        return false;
    }
}

/** True when the page renders dark: an explicit [data-theme] on <html> beats the OS scheme. */
export function prefersDark() {
    try {
        const explicit = document.documentElement.dataset.theme;
        if (explicit) return explicit === 'dark';
        return window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false;
    } catch {
        return false;
    }
}

function readStoredTier() {
    try {
        const stored = localStorage.getItem(STORAGE_KEY);
        return TIERS.includes(stored) ? stored : null;
    } catch {
        return null; // Private mode / blocked storage.
    }
}

function notifyTierChanged() {
    for (const listener of state.listeners) {
        try {
            listener(state.tier);
        } catch {
            // A misbehaving effect must not stop the others from being told.
        }
    }
}

function frame(now) {
    state.rafHandle = 0;

    const elapsed = state.lastFrameStart ? now - state.lastFrameStart : 0;
    state.lastFrameStart = now;

    const workStart = performance.now();

    for (const task of state.tasks.values()) {
        try {
            task.callback(now, elapsed);
        } catch (err) {
            // One broken effect must not take the whole loop down with it.
            console.error(`[gfx] task "${task.name}" threw; unregistering`, err);
            state.tasks.delete(task.id);
        }
    }

    const workMs = performance.now() - workStart;
    recordFrame(elapsed, workMs, now);

    if (state.tasks.size > 0) {
        state.rafHandle = requestAnimationFrame(frame);
    } else {
        state.lastFrameStart = 0;
    }
}

function recordFrame(elapsedMs, workMs, now) {
    const stats = state.stats;
    stats.activeTasks = state.tasks.size;

    if (elapsedMs <= 0) {
        return;
    }

    stats.sampledFrames++;
    state.frameMsAccumulator += elapsedMs;
    state.cpuMsAccumulator += workMs;
    state.frameMsCount++;

    if (elapsedMs > stats.worstFrameMs) {
        stats.worstFrameMs = elapsedMs;
    }

    if (elapsedMs > FRAME_BUDGET_MS) {
        stats.droppedFrames++;
        // Each frame counts for at most HITCH_CAP_MS. One long stall — a first shader compile, a
        // GC, a route render — is a hitch, not sustained load, and uncapped a single 1.6s frame
        // tripped the downgrade on its own and killed the effect whose compile caused it.
        state.overBudgetStreak += Math.min(elapsedMs, HITCH_CAP_MS);

        if (state.overBudgetStreak >= OVER_BUDGET_MS_BEFORE_DOWNGRADE) {
            autoDowngrade();
        }
    } else {
        state.overBudgetStreak = 0;
    }

    // Flush the rolling average about four times a second — often enough for a live readout,
    // rare enough that the diagnostics overlay is not itself a source of jank.
    if (now - state.lastStatsFlush >= 250) {
        stats.frameMs = state.frameMsAccumulator / Math.max(1, state.frameMsCount);
        stats.cpuMs = state.cpuMsAccumulator / Math.max(1, state.frameMsCount);
        stats.fps = stats.frameMs > 0 ? 1000 / stats.frameMs : 0;
        state.frameMsAccumulator = 0;
        state.cpuMsAccumulator = 0;
        state.frameMsCount = 0;
        state.lastStatsFlush = now;
    }
}

/**
 * Steps the tier down one level after sustained overrun. This is the mechanism that turns
 * "should hit 60 FPS" into "does, or stops trying" — without it, a mid-range phone just
 * renders everything badly forever.
 */
function autoDowngrade() {
    const index = TIERS.indexOf(state.tier);
    if (index <= 0) {
        state.overBudgetStreak = 0;
        return;
    }

    const next = TIERS[index - 1];
    console.warn(`[gfx] sustained frame overrun; dropping effects tier ${state.tier} -> ${next}`);
    state.tier = next;
    state.tierWasAutoDowngraded = true;
    state.overBudgetStreak = 0;

    // Deliberately NOT persisted. A downgrade caused by one heavy page or a background tab
    // stealing the GPU should not silently become the user's permanent setting.
    notifyTierChanged();
}

function ensureLoopRunning() {
    if (!state.rafHandle && state.tasks.size > 0 && !document.hidden) {
        state.lastFrameStart = 0;
        state.rafHandle = requestAnimationFrame(frame);
    }
}

function stopLoop() {
    if (state.rafHandle) {
        cancelAnimationFrame(state.rafHandle);
        state.rafHandle = 0;
    }
    state.lastFrameStart = 0;
}

// A hidden tab keeps its rAF callbacks queued in some browsers and throttled in others; either
// way, animating a page nobody is looking at is pure battery cost.
document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
        stopLoop();
    } else {
        ensureLoopRunning();
    }
});

export const gfx = {
    init() {
        state.reducedMotion = detectReducedMotion();
        state.webgl2 = detectWebGl2();

        // 'full' unless the user picked otherwise in /settings: the frame-budget watchdog steps a
        // device that cannot keep up down to 'lite' on its own. A stored preference is still
        // overridden by an OS-level reduced-motion request and by a device with no WebGL2 —
        // neither is a preference we are entitled to ignore.
        state.tier = (state.reducedMotion || !state.webgl2)
            ? 'off'
            : (readStoredTier() ?? 'full');

        return this.describe();
    },

    describe() {
        return {
            tier: state.tier,
            reducedMotion: state.reducedMotion,
            webgl2: state.webgl2,
            autoDowngraded: state.tierWasAutoDowngraded
        };
    },

    /** True when the requested level is at or below what this device is currently running. */
    allows(level) {
        return TIERS.indexOf(state.tier) >= TIERS.indexOf(level);
    },

    setTier(tier) {
        if (!TIERS.includes(tier)) {
            return state.tier;
        }

        if (state.reducedMotion && tier !== 'off') {
            // Honour the OS. Offering the control and then ignoring it is worse than hiding it.
            return state.tier;
        }

        state.tier = tier;
        state.tierWasAutoDowngraded = false;
        try {
            localStorage.setItem(STORAGE_KEY, tier);
        } catch {
            // Preference simply will not persist; the session still respects it.
        }
        notifyTierChanged();
        return state.tier;
    },

    onTierChanged(listener) {
        state.listeners.add(listener);
        return () => state.listeners.delete(listener);
    },

    /** Registers a per-frame callback. Returns an unregister function. */
    addTask(name, callback) {
        const id = state.nextTaskId++;
        state.tasks.set(id, { id, name, callback });
        ensureLoopRunning();
        return () => {
            state.tasks.delete(id);
            if (state.tasks.size === 0) {
                stopLoop();
            }
        };
    },

    /** Frame-budget readout. Console: `poseeFx.stats()`. */
    stats() {
        return { ...state.stats, tier: state.tier, autoDowngraded: state.tierWasAutoDowngraded };
    }
};

// ── Minimal WebGL2 helpers ───────────────────────────────────────────────────────────────
//
// Hand-rolled rather than pulled from a library: the backdrop is one fullscreen triangle.

/**
 * A WebGL2 render target on its own canvas. Per frame: `if (!surface.beginFrame()) return;`
 * … draw … `surface.present();`. On teardown: `surface.release()`. Returns null without WebGL2.
 */
export function createSurface(canvas, { maxDpr = 2 } = {}) {
    let gl = null;
    try {
        gl = canvas.getContext('webgl2', {
            alpha: true,
            antialias: false,      // Post-process passes; MSAA buys nothing and costs fill rate.
            depth: false,
            stencil: false,
            premultipliedAlpha: true,
            powerPreference: 'low-power'
        });
    } catch {
        gl = null;
    }
    if (!gl) return null;

    // Capping DPR matters: a DPR-3 phone would otherwise ask a mobile GPU for nine times the fill
    // rate of the CSS pixel count.
    const measure = () => {
        const dpr = Math.min(window.devicePixelRatio || 1, maxDpr);
        return {
            width: Math.max(1, Math.floor((canvas.clientWidth || canvas.width || 1) * dpr)),
            height: Math.max(1, Math.floor((canvas.clientHeight || canvas.height || 1) * dpr))
        };
    };

    const surface = {
        gl,
        canvas,
        ...measure(),
        dead: false,

        beginFrame() {
            if (surface.dead) return false;
            const { width, height } = measure();
            if (canvas.width !== width || canvas.height !== height) {
                canvas.width = width;
                canvas.height = height;
            }
            surface.width = canvas.width;
            surface.height = canvas.height;
            surface.bindTarget();
            gl.disable(gl.BLEND);
            gl.disable(gl.DEPTH_TEST);
            gl.disable(gl.SCISSOR_TEST);
            return true;
        },

        /** Re-binds the default framebuffer, for multi-pass effects returning from their own FBO. */
        bindTarget() {
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
            gl.viewport(0, 0, canvas.width, canvas.height);
        },

        // Nothing to copy: the effect drew straight to the visible canvas.
        present: () => !surface.dead,

        release() {
            if (surface.dead) return;
            surface.dead = true;
            try { gl.getExtension('WEBGL_lose_context')?.loseContext(); } catch { /* optional */ }
        }
    };
    return surface;
}

export function compileProgram(gl, vertexSource, fragmentSource) {
    const compile = (type, source) => {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
            const log = gl.getShaderInfoLog(shader);
            gl.deleteShader(shader);
            throw new Error(`shader compile failed: ${log}`);
        }
        return shader;
    };

    const vs = compile(gl.VERTEX_SHADER, vertexSource);
    const fs = compile(gl.FRAGMENT_SHADER, fragmentSource);
    const program = gl.createProgram();
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);

    // Shader objects are reference-counted by the program; detaching lets the driver free the
    // source immediately instead of holding it for the page's lifetime.
    gl.detachShader(program, vs);
    gl.detachShader(program, fs);
    gl.deleteShader(vs);
    gl.deleteShader(fs);

    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        const log = gl.getProgramInfoLog(program);
        gl.deleteProgram(program);
        throw new Error(`program link failed: ${log}`);
    }

    return program;
}

/**
 * Vertex shader for a single triangle that covers the viewport. Preferred over two triangles:
 * no diagonal seam, and the GPU rasterises one primitive instead of two.
 */
export const FULLSCREEN_VERTEX_SHADER = `#version 300 es
out vec2 vUv;
void main() {
    vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    vUv = pos;
    gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}`;
