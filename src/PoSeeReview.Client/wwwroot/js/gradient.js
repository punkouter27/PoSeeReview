// Strangeness-reactive background. A single fullscreen triangle running domain-warped value
// noise, lit by three moving lights, with hue, warp strength and speed driven by the current
// comic's score — so a 92 visibly seethes and a 30 drifts. The fill light leans toward the
// pointer, scrolling nudges the field along, and the key light swells with whatever the app is
// playing. All three are uniforms into the one existing pass; none of them adds a texture read.
//
// LIGHTING, AND WHY IT IS AFFORDABLE HERE.
//
// There is no geometry in this scene. What there is instead is the noise field itself, read as a
// HEIGHT MAP: the same fbm that produces the colour also produces a surface, and a surface has a
// normal. That turns a flat colour ramp into something with form, for the cost of two extra fbm
// evaluations (central differences) rather than a mesh, a depth buffer, and a shadow map.
//
// Three lights, but only ONE casts a shadow. The key light's occlusion is a three-tap march
// along its direction against the height field — enough to darken the lee side of a ridge, which
// is all a viewer can perceive on a backdrop this soft. Shadowing all three would triple that
// cost to produce overlapping penumbrae nobody can see through the content sitting on top.
//
// Cost control, in order of how much it matters:
//   * DPR is capped at 1.25 here (lower than the shared default). This is an out-of-focus
//     backdrop; nobody can see the difference, and fill rate is the entire cost of the pass.
//   * The whole thing runs on the shared scheduler, so it pauses with the tab.
//   * 'full' tier only. At 'lite' the CSS gradient underneath is what shows.

import { gfx, createSurface, compileProgram, FULLSCREEN_VERTEX_SHADER, prefersDark } from './gfx-core.js';

const BACKDROP_UNIFORMS = `
uniform float uTime;
uniform vec2  uResolution;
uniform float uStrange;    // 0..1
uniform vec3  uColorA;
uniform vec3  uColorB;
uniform vec3  uColorC;
uniform vec3  uKeyLight;   // rgb tint of the shadow-casting light
uniform float uAmbient;
uniform vec2  uPointer;    // 0..1, origin bottom-left, eased
uniform float uScroll;     // 0..1 down the page, eased
uniform float uLevel;      // 0..1 audio level, eased
uniform float uKick;       // 1 when a score lands, decaying to 0
uniform float uKickAge;    // seconds since it landed
`;

const BACKDROP_GLSL = `
// An integer hash of the cell. The float one it replaced (fract(p * 123.34)...) tore the field into
// vertical seams on some GPUs (Adreno via D3D11): the compiler reassociated the arithmetic, so a
// cell and its neighbour rounded differently, and near a whole number fract() flipped 0.999 to
// 0.001. Integer maths has no rounding to drift.
float posee_hash(vec2 p) {
    uvec2 q = uvec2(ivec2(floor(p)) + 32768);
    uint h = (q.x * 1597334677u) ^ (q.y * 3812015801u);
    h = (h ^ (h >> 16u)) * 2246822519u;
    h ^= h >> 13u;
    return float(h) * (1.0 / 4294967295.0);
}

float posee_valueNoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);

    float a = posee_hash(i);
    float b = posee_hash(i + vec2(1.0, 0.0));
    float c = posee_hash(i + vec2(0.0, 1.0));
    float d = posee_hash(i + vec2(1.0, 1.0));

    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float posee_fbm(vec2 p) {
    float total = 0.0;
    float amplitude = 0.5;
    for (int i = 0; i < 3; i++) {
        total += posee_valueNoise(p) * amplitude;
        p *= 2.0;
        amplitude *= 0.5;
    }
    return total;
}

float posee_field(vec2 p, vec2 warp, float warpAmount, float t) {
    return posee_fbm(p * 2.1 + warp * warpAmount + t * 0.35);
}

struct PoseeScene {
    float energy;
    float t;
    float warpAmount;
    float aspect;
    vec2  centre;
};

PoseeScene posee_scene() {
    PoseeScene s;
    s.energy = uStrange;
    s.t = uTime * (0.02 + s.energy * 0.10) + uScroll * 0.6;
    s.warpAmount = 0.15 + s.energy * 0.85;
    s.aspect = uResolution.x / max(uResolution.y, 1.0);
    s.centre = vec2(s.aspect, 1.0) * 0.5;
    return s;
}

vec2 posee_warp(vec2 p, PoseeScene s) {
    return vec2(
        posee_fbm(p * 1.4 + vec2(s.t, s.t * 0.7)),
        posee_fbm(p * 1.4 + vec2(-s.t * 0.8, s.t * 1.1) + 5.2)
    );
}

vec3 posee_backdrop(vec2 p, PoseeScene s) {
    vec2 warp = posee_warp(p, s);
    float n = posee_field(p, warp, s.warpAmount, s.t);
    float eps = 0.012;
    float hX = posee_field(p + vec2(eps, 0.0), warp, s.warpAmount, s.t);
    float hY = posee_field(p + vec2(0.0, eps), warp, s.warpAmount, s.t);
    vec3 normal = normalize(vec3((n - hX) / eps, (n - hY) / eps, 0.55));

    vec3 keyPos  = vec3(s.centre + vec2(cos(uTime * 0.13), sin(uTime * 0.17)) * 0.42, 0.55);
    vec2 fillOrbit = s.centre + vec2(cos(-uTime * 0.09 + 2.1), sin(-uTime * 0.11 + 2.1)) * 0.55;
    vec3 fillPos = vec3(mix(fillOrbit, uPointer * vec2(s.aspect, 1.0), 0.35), 0.40);
    vec3 rimPos  = vec3(s.centre + vec2(cos(uTime * 0.21 + 4.2), sin(uTime * 0.19 + 4.2)) * 0.68, 0.28);

    vec3 surface = vec3(p, n * 0.35);

    vec3 keyDir  = normalize(keyPos  - surface);
    vec3 fillDir = normalize(fillPos - surface);
    vec3 rimDir  = normalize(rimPos  - surface);

    float keyFall  = 1.0 / (1.0 + dot(keyPos.xy  - p, keyPos.xy  - p) * 2.2);
    float fillFall = 1.0 / (1.0 + dot(fillPos.xy - p, fillPos.xy - p) * 2.6);
    float rimFall  = 1.0 / (1.0 + dot(rimPos.xy  - p, rimPos.xy  - p) * 3.4);

    float shadow = 1.0;
    vec2 stepDir = normalize(keyPos.xy - p) * 0.05;
    for (int i = 1; i <= 3; i++) {
        vec2 samplePoint = p + stepDir * float(i);
        float height = posee_field(samplePoint, warp, s.warpAmount, s.t);
        float rayHeight = n + (keyPos.z - n * 0.35) * (float(i) / 4.0);
        shadow -= max(0.0, height - rayHeight) * 0.55;
    }
    shadow = clamp(shadow, 0.35, 1.0);

    float keyDiffuse  = max(0.0, dot(normal, keyDir))  * keyFall  * shadow;
    float fillDiffuse = max(0.0, dot(normal, fillDir)) * fillFall;
    float rimDiffuse  = max(0.0, dot(normal, rimDir))  * rimFall;

    vec3 albedo = mix(uColorA, uColorB, smoothstep(0.25, 0.75, n));
    albedo = mix(albedo, uColorC, smoothstep(0.55, 1.0, n) * (0.25 + uStrange * 0.55));

    vec3 color = albedo * uAmbient;
    color += albedo * uKeyLight * keyDiffuse * (1.30 + uLevel * 0.45);
    color += albedo * uColorC   * fillDiffuse * 0.55;
    color += uColorC * rimDiffuse * (0.22 + uLevel * 0.15);

    return color;
}

float posee_height(vec2 p, PoseeScene s) {
    return posee_field(p, posee_warp(p, s), s.warpAmount, s.t);
}
`;

const FRAGMENT_SHADER = `#version 300 es
// highp int: the hash is 32-bit integer maths, which a 16-bit mediump int would wrap.
precision mediump float;
precision highp int;

in vec2 vUv;
out vec4 fragColor;
${BACKDROP_UNIFORMS}
${BACKDROP_GLSL}

void main() {
    vec2 uv = vUv;
    // Correct for aspect so the noise cells stay round on a wide viewport. This is the space the
    // glass pass also works in, which is what lets a pane refract into the scene around it.
    PoseeScene s = posee_scene();

    // Past ~55 the page itself starts to run: a slow swirl about the centre and drips that pull
    // the lower half down. A 20 never melts; a 95 visibly does.
    vec2 c = uv - 0.5;
    float r = length(c * vec2(s.aspect, 1.0));
    float melt = smoothstep(0.55, 1.0, uStrange);
    float swirl = melt * 0.35 * sin(uTime * 0.4 + r * 5.0);
    uv = 0.5 + mat2(cos(swirl), -sin(swirl), sin(swirl), cos(swirl)) * c;
    uv.y += melt * 0.035 * sin(uv.x * 9.0 + uTime * 0.9) * (1.0 - uv.y);

    // The score landing sends one ripple out from the middle of the page, harder the stranger.
    float band = r - uKickAge * 0.75;
    float ring = uKick * exp(-band * band * 90.0);
    uv += (c / max(r, 1e-3)) * ring * 0.03 * (0.4 + uStrange);

    vec2 p = uv * vec2(s.aspect, 1.0);

    vec3 color = posee_backdrop(p, s);
    color += uColorC * ring * 0.18;

    // Vignette keeps the centre of the page readable; content sits on top of this.
    float vignette = smoothstep(1.25, 0.25, length(uv - 0.5) * 1.4);
    color *= 0.82 + vignette * 0.18;

    // Dithering. Without it, a slow wide gradient shows visible banding on 8-bit displays and
    // the whole effect reads as a compression artifact.
    float dither = (posee_hash(gl_FragCoord.xy) - 0.5) / 255.0;
    fragColor = vec4(clamp(color + dither, 0.0, 1.0), 1.0);
}`;

// There is one backdrop, so there is one scene and one handle. The scene outlives its GL program:
// a tier downgrade or a lost context PAUSES it (program gone, loop gone, CSS background back) and
// keeps the canvas and score, so climbing back to 'full' or a restored context can resume it.
const HANDLE = 1;
let scene = null;

// Warm key against the cool violet field: the complementary split is what stops a single-hue
// noise field from reading as flat.
const KEY_LIGHT = [1.00, 0.86, 0.62];

const clamp01 = (value) => Math.min(1, Math.max(0, value));

function parseColor(hex, fallback) {
    const match = /^#?([0-9a-f]{6})$/i.exec((hex ?? '').trim());
    if (!match) return fallback;
    const int = parseInt(match[1], 16);
    return [((int >> 16) & 255) / 255, ((int >> 8) & 255) / 255, (int & 255) / 255];
}

/** Per-frame approach toward a target colour, in place. No allocation in the draw loop. */
function easeColor(current, target) {
    current[0] += (target[0] - current[0]) * COLOR_EASE;
    current[1] += (target[1] - current[1]) * COLOR_EASE;
    current[2] += (target[2] - current[2]) * COLOR_EASE;
}

/**
 * Slower than the score ease (0.04) on purpose. The score is a number changing; this is the
 * whole page changing colour, and at the same rate it reads as a glitch rather than a mood.
 */
const COLOR_EASE = 0.018;

// ── The scene's base colours come from the design tokens ─────────────────────────────────
//
// They used to be three hardcoded dark purples, which was survivable only because this canvas
// was not actually visible: `.page` painted an opaque background straight over it. Now that the
// page stands aside for a running backdrop, the field IS the page's ground, and a ground that
// ignores the theme is a page whose text contrast is unmeasured.
//
// So the scene is built from --color-surface / --color-brand-surface / --color-brand, exactly
// like the CSS fallback on .fx-backdrop that it replaces. In light mode that is a pale lavender
// field; in dark mode the same three tokens have already flipped. Reading them rather than
// restating them matters: a literal freezes one theme into a page that renders both.

const SCENE_FALLBACK = {
    light: [[0.976, 0.973, 1.0], [0.929, 0.914, 1.0], [0.486, 0.227, 0.929]],
    dark: [[0.10, 0.03, 0.20], [0.16, 0.09, 0.30], [0.58, 0.36, 0.96]]
};

/**
 * The three base colours and the ambient floor for the current theme.
 *
 * The ambient is the part that is easy to get wrong. On a dark ground 0.62 leaves the lights
 * somewhere to go; on a light ground the same value renders a white-ish albedo as mid-grey, and
 * every text token in the app is measured against a near-white surface.
 */
function readSceneTokens() {
    const dark = prefersDark();
    const fallback = dark ? SCENE_FALLBACK.dark : SCENE_FALLBACK.light;

    try {
        const style = getComputedStyle(document.documentElement);
        const pick = (name, or) => parseColor(style.getPropertyValue(name).trim(), or);
        return {
            dark,
            ambient: dark ? 0.62 : 0.94,
            colors: [
                pick('--color-surface', fallback[0]),
                pick('--color-brand-surface', fallback[1]),
                pick('--color-brand', fallback[2])
            ]
        };
    } catch {
        return { dark, ambient: dark ? 0.62 : 0.94, colors: fallback };
    }
}

/**
 * @param {HTMLCanvasElement} canvas
 * @param {{ score?: number, level?: () => number }} options `level` is the audio meter, passed in
 *        by fx.js: this module does not know the app makes noise.
 * @returns {number} handle, or 0 with no canvas. A handle does not promise the shader is drawing
 *          — below 'full' the scene is remembered and waits.
 */
export function start(canvas, options = {}) {
    if (!canvas) {
        return 0;
    }
    stop(HANDLE);

    const strange = clamp01((options.score ?? 40) / 100);
    scene = {
        canvas,
        level: typeof options.level === 'function' ? options.level : null,
        surface: null,
        program: null,
        vao: null,
        uniforms: null,
        task: null,
        startTime: performance.now(),
        strange,
        targetStrange: strange,
        colorA: [0, 0, 0], colorB: [0, 0, 0], colorC: [0, 0, 0],
        targetA: [0, 0, 0], targetB: [0, 0, 0], targetC: [0, 0, 0],
        ambient: 0.62,
        pointer: [0.5, 0.5],
        targetPointer: [0.5, 0.5],
        scroll: 0,
        targetScroll: 0,
        audioLevel: 0,
        kickAt: -Infinity
    };

    canvas.addEventListener('webglcontextlost', onContextLost);
    canvas.addEventListener('webglcontextrestored', resume);
    resume();
    return HANDLE;
}

function resume() {
    const s = scene;
    if (!s || s.task || !gfx.allows('full')) return;

    s.surface ??= createSurface(s.canvas, { maxDpr: 1.25 });
    if (!s.surface) return;

    const { gl } = s.surface;
    if (gl.isContextLost()) return;

    try {
        s.program = compileProgram(gl, FULLSCREEN_VERTEX_SHADER, FRAGMENT_SHADER);
    } catch (err) {
        // A driver that refuses this shader is not an app error — the CSS gradient stays.
        console.warn('[gradient] shader unavailable; falling back to CSS', err);
        return;
    }

    s.uniforms = Object.fromEntries(
        ['Time', 'Resolution', 'Strange', 'ColorA', 'ColorB', 'ColorC', 'KeyLight', 'Ambient',
            'Pointer', 'Scroll', 'Level', 'Kick', 'KickAge']
            .map((name) => [name, gl.getUniformLocation(s.program, `u${name}`)]));

    // WebGL2 requires a bound VAO even when every vertex is generated from gl_VertexID.
    s.vao = gl.createVertexArray();

    // Snapped, not eased: nothing was on screen to ease from.
    const tokens = readSceneTokens();
    for (const [live, target, value] of [
        [s.colorA, s.targetA, tokens.colors[0]],
        [s.colorB, s.targetB, tokens.colors[1]],
        [s.colorC, s.targetC, tokens.colors[2]]
    ]) {
        live.splice(0, 3, ...value);
        target.splice(0, 3, ...value);
    }
    s.ambient = tokens.ambient;

    window.addEventListener('pointermove', onPointerMove, { passive: true });
    window.addEventListener('scroll', onScroll, { passive: true });
    onScroll();

    s.task = gfx.addTask('gradient', draw);
    notifyActive(true);
}

function draw(now) {
    const s = scene;
    const { gl } = s.surface;
    if (!s.surface.beginFrame()) return;

    // Ease toward the target so a score change is a transition, not a jump cut.
    s.strange += (s.targetStrange - s.strange) * 0.04;

    // Colours ease toward their targets too, so a theme flip is not a hard cut.
    easeColor(s.colorA, s.targetA);
    easeColor(s.colorB, s.targetB);
    easeColor(s.colorC, s.targetC);

    s.pointer[0] += (s.targetPointer[0] - s.pointer[0]) * 0.06;
    s.pointer[1] += (s.targetPointer[1] - s.pointer[1]) * 0.06;
    s.scroll += (s.targetScroll - s.scroll) * 0.08;

    // Fast up, slow down, like a meter: a note is over in a few frames and the light should
    // swell with it and then let go, not flicker.
    const level = s.level ? clamp01(s.level()) : 0;
    s.audioLevel += (level - s.audioLevel) * (level > s.audioLevel ? 0.5 : 0.06);

    const u = s.uniforms;
    gl.useProgram(s.program);
    gl.bindVertexArray(s.vao);
    gl.uniform1f(u.Time, (now - s.startTime) / 1000);
    gl.uniform2f(u.Resolution, s.surface.width, s.surface.height);
    gl.uniform1f(u.Strange, s.strange);
    gl.uniform3fv(u.ColorA, s.colorA);
    gl.uniform3fv(u.ColorB, s.colorB);
    gl.uniform3fv(u.ColorC, s.colorC);
    gl.uniform3fv(u.KeyLight, KEY_LIGHT);
    gl.uniform1f(u.Ambient, s.ambient);
    gl.uniform2fv(u.Pointer, s.pointer);
    gl.uniform1f(u.Scroll, s.scroll);
    gl.uniform1f(u.Level, s.audioLevel);
    const kickAge = (now - s.kickAt) / 1000;
    gl.uniform1f(u.Kick, kickAge < KICK_SECONDS ? 1 - kickAge / KICK_SECONDS : 0);
    gl.uniform1f(u.KickAge, Math.min(kickAge, KICK_SECONDS));
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.bindVertexArray(null);

    s.surface.present();
}

/** Stops drawing and hands the ground back to CSS, keeping what resume() needs. */
function pause() {
    const s = scene;
    if (!s?.task) return;

    s.task();
    s.task = null;
    window.removeEventListener('pointermove', onPointerMove);
    window.removeEventListener('scroll', onScroll);
    notifyActive(false);

    try {
        // All no-ops on a lost context. Shrinking and clearing drops the fullscreen backing
        // store and the last frame with it, so the canvas's own CSS background shows through.
        const { gl } = s.surface;
        gl.deleteProgram(s.program);
        gl.deleteVertexArray(s.vao);
        s.canvas.width = s.canvas.height = 1;
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
    } catch {
        // The loop is already stopped, which is the part that matters.
    }
    s.program = s.vao = s.uniforms = null;
}

function onContextLost(event) {
    // Without preventDefault the browser never offers the context back.
    event.preventDefault();
    pause();
}

function onPointerMove(event) {
    if (!scene) return;
    scene.targetPointer[0] = clamp01(event.clientX / (window.innerWidth || 1));
    // GL's origin is the bottom-left corner; the pointer's is the top-left.
    scene.targetPointer[1] = 1 - clamp01(event.clientY / (window.innerHeight || 1));
}

function onScroll() {
    if (!scene) return;
    const page = document.scrollingElement;
    const range = page ? page.scrollHeight - page.clientHeight : 0;
    scene.targetScroll = range > 0 ? clamp01(page.scrollTop / range) : 0;
}

export function setScore(id, score) {
    if (scene) {
        scene.targetStrange = clamp01((score ?? 0) / 100);
    }
}

const KICK_SECONDS = 1.6;

/** The score just landed: one ripple out from the centre. Retunes to the score too. */
export function kick(score) {
    if (!scene) return;
    if (typeof score === 'number') scene.targetStrange = clamp01(score / 100);
    scene.kickAt = performance.now();
}

/**
 * Re-reads the tokens. Bound to a theme change below rather than polled: the token
 * values change under the shader with no event of their own, and a backdrop still drawing light
 * mode's ground under a dark page is the most visible bug this file can have.
 */
function refreshTheme() {
    if (!scene) return;
    const tokens = readSceneTokens();
    scene.targetA = [...tokens.colors[0]];
    scene.targetB = [...tokens.colors[1]];
    scene.targetC = [...tokens.colors[2]];
    scene.ambient = tokens.ambient;
}

try {
    // The OS-level flip, and an explicit [data-theme] on <html>, which beats it.
    window.matchMedia?.('(prefers-color-scheme: dark)').addEventListener('change', refreshTheme);
    new MutationObserver(refreshTheme)
        .observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
} catch {
    // Backdrop keeps the colours it started with.
}

function notifyActive(drawing) {
    // The handshake that lets `.page` stand aside. app.css keys the page's own opaque background
    // off the ABSENCE of this flag, so the ground is only ever handed over to a shader that is
    // genuinely drawing — every failure path (no WebGL2, reduced motion, a downgraded tier, a
    // lost context) leaves the CSS background exactly where it was.
    try {
        if (drawing) {
            document.documentElement.dataset.fxBackdrop = 'on';
        } else {
            delete document.documentElement.dataset.fxBackdrop;
        }
    } catch {
        // Without the flag the page keeps its own background; the shader is simply covered.
    }
}

export function stop(id) {
    if (!scene) return;

    pause();
    scene.canvas.removeEventListener('webglcontextlost', onContextLost);
    scene.canvas.removeEventListener('webglcontextrestored', resume);
    scene.surface?.release();
    scene = null;
}

// A downgrade out of 'full' gives the GPU back; a return to 'full' picks the scene up again.
gfx.onTierChanged((tier) => (tier === 'full' ? resume() : pause()));
