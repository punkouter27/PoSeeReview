// Two small WebGL accents on the comic page, `full` tier only, each on its own short-lived canvas.
//
//  - bleed(container, at): a one-shot ink bleed that spreads along a panel boundary as the reader
//    scrolls the next panel to the middle of the screen, then soaks away (~0.9s).
//  - heat(ring, score): a heat shimmer rising behind the score ring for stranger places (past
//    HEAT_FROM). It lives while the ring is in the document and the tier stays `full`.
//
// Both draw through gfx-core's shared loop and surfaces; a failed context simply means no accent.

import { gfx, createSurface, compileProgram, FULLSCREEN_VERTEX_SHADER } from './gfx-core.js';

export const HEAT_FROM = 55;
const BLEED_MS = 900;

function readRgb(name, fallback) {
    const hex = /^#?([0-9a-f]{6})$/i.exec(getComputedStyle(document.documentElement).getPropertyValue(name).trim())?.[1];
    const n = hex ? parseInt(hex, 16) : fallback;
    return [(n >> 16) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
}

const NOISE_GLSL = `
float hash(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
float noise(vec2 p) {
    vec2 i = floor(p), f = fract(p), u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}
float fbm(vec2 p) { float v = 0.0, a = 0.5; for (int i = 0; i < 4; i++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }`;

const BLEED_FS = `#version 300 es
precision highp float;
in vec2 vUv;
out vec4 outColor;
uniform float uT;        // 0..1 through the bleed
uniform float uAt;       // boundary, 0 top .. 1 bottom
uniform float uAspect;   // width / height
uniform vec3 uInk;
${NOISE_GLSL}
void main() {
    vec2 p = vec2(vUv.x, 1.0 - vUv.y);
    float spread = smoothstep(0.0, 0.55, uT);                // runs out from the centre
    float soak = 1.0 - smoothstep(0.45, 1.0, uT);            // then soaks into the paper
    float dx = abs(p.x - 0.5) * 2.0;
    float n = fbm(vec2(p.x * 9.0 * uAspect, p.y * 9.0) + uT * 1.5);
    float width = (0.012 + 0.05 * n) * spread * (1.0 - dx * dx * 0.6);
    float edge = abs(p.y - uAt) - width;
    float reach = dx - spread * 1.1;
    float ink = (1.0 - smoothstep(0.0, 0.01, edge)) * (1.0 - smoothstep(-0.05, 0.0, reach));
    float a = ink * soak * 0.6;
    outColor = vec4(uInk * a, a);
}`;

const HEAT_FS = `#version 300 es
precision highp float;
in vec2 vUv;
out vec4 outColor;
uniform float uTime;
uniform float uHeat;     // 0..1 above HEAT_FROM
uniform vec3 uHot;
uniform vec3 uCore;
${NOISE_GLSL}
void main() {
    vec2 p = vUv * 2.0 - 1.0;
    float r = length(p);
    // Flame tongues rising: noise scrolled upward, stretched vertically, warped sideways.
    vec2 q = vec2(p.x * 2.2 + fbm(p * 3.0 + uTime * 0.6) * 0.6, p.y * 1.4 - uTime * (0.9 + uHeat));
    float flame = fbm(q * 2.5);
    float ring = smoothstep(1.0, 0.62, r) * smoothstep(0.38, 0.6, r);
    float rise = smoothstep(-0.9, 0.6, p.y);
    float a = clamp(ring * flame * rise * (0.35 + uHeat * 0.9), 0.0, 1.0);
    vec3 c = mix(uCore, uHot, smoothstep(0.3, 0.8, flame));
    outColor = vec4(c * a, a);
}`;

function overlay(host, className) {
    const canvas = document.createElement('canvas');
    canvas.className = className;
    canvas.setAttribute('aria-hidden', 'true');
    host.appendChild(canvas);
    const surface = createSurface(canvas, { maxDpr: 1.5 });
    if (!surface) {
        canvas.remove();
        return null;
    }
    return { canvas, surface };
}

function blendOn(gl) {
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
}

/** A one-shot ink bleed across `container` at `at` (0 top .. 1 bottom). */
export function bleed(container, at) {
    if (!container?.isConnected || !gfx.allows('full')) return false;
    const made = overlay(container, 'fx-ink-bleed');
    if (!made) return false;
    const { canvas, surface } = made;
    const gl = surface.gl;
    let program;
    try {
        program = compileProgram(gl, FULLSCREEN_VERTEX_SHADER, BLEED_FS);
    } catch (error) {
        console.warn('[ink-fx] bleed', error);
        surface.release();
        canvas.remove();
        return false;
    }
    const u = (name) => gl.getUniformLocation(program, name);
    const ink = readRgb('--color-comic-ink', 0x111111);
    const t0 = performance.now();
    const stop = gfx.addTask('ink-bleed', (now) => {
        const t = (now - t0) / BLEED_MS;
        if (t >= 1 || !canvas.isConnected || !gfx.allows('full') || !surface.beginFrame()) {
            stop();
            surface.release();
            canvas.remove();
            return;
        }
        blendOn(gl);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.useProgram(program);
        gl.uniform1f(u('uT'), t);
        gl.uniform1f(u('uAt'), at);
        gl.uniform1f(u('uAspect'), surface.width / Math.max(1, surface.height));
        gl.uniform3fv(u('uInk'), ink);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    });
    return true;
}

/** Heat haze behind the score ring for scores past HEAT_FROM. Returns a stop function, or null. */
export function heat(ring, score) {
    const heatLevel = (Math.min(100, score) - HEAT_FROM) / (100 - HEAT_FROM);
    if (!ring?.isConnected || heatLevel <= 0 || !gfx.allows('full')) return null;
    const made = overlay(ring, 'fx-heat');
    if (!made) return null;
    const { canvas, surface } = made;
    const gl = surface.gl;
    let program;
    try {
        program = compileProgram(gl, FULLSCREEN_VERTEX_SHADER, HEAT_FS);
    } catch (error) {
        console.warn('[ink-fx] heat', error);
        surface.release();
        canvas.remove();
        return null;
    }
    const u = (name) => gl.getUniformLocation(program, name);
    const hot = readRgb('--color-accent', 0xf59e0b);
    const core = readRgb('--color-brand', 0x7c3aed);
    const t0 = performance.now();
    let done = false;
    const stopTask = gfx.addTask('score-heat', (now) => {
        if (!canvas.isConnected || !gfx.allows('full') || !surface.beginFrame()) {
            finish();
            return;
        }
        blendOn(gl);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.useProgram(program);
        gl.uniform1f(u('uTime'), (now - t0) / 1000);
        gl.uniform1f(u('uHeat'), heatLevel);
        gl.uniform3fv(u('uHot'), hot);
        gl.uniform3fv(u('uCore'), core);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    });
    function finish() {
        if (done) return;
        done = true;
        stopTask();
        surface.release();
        canvas.remove();
    }
    return finish;
}
