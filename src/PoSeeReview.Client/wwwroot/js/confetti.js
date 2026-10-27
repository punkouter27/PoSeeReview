// Confetti made of tiny comic panels, for a new number one on the Hall of Fame — and, further
// down, the ink splat thrown off the CERTIFIED STRANGE stamp.
//
// Each piece is a little inked frame — a token-coloured fill, a halftone corner, a heavy
// --color-comic-ink border — that tumbles in 3D (its height is scaled by the cosine of its
// flip, which reads as a card turning over), sways, and falls under drag. 2D canvas, not WebGL:
// it is a couple of hundred rectangles for three seconds, and a GL context for that is all
// setup cost. Runs at `lite` too; `off` (reduced motion) gets nothing.

import { gfx } from './gfx-core.js';

const DURATION_MS = 3200;
const GRAVITY = 1400;      // px/s²
const DRAG = 1.6;          // 1/s

let active = null;

function tokens() {
    const style = getComputedStyle(document.documentElement);
    const read = (name, fallback) => style.getPropertyValue(name).trim() || fallback;
    return {
        ink: read('--color-comic-ink', '#111111'),
        fills: ['--color-accent', '--color-brand', '--color-brand-alt', '--color-success', '--color-danger', '--color-card']
            .map((name) => read(name, '#ffcc00'))
    };
}

/**
 * Bursts confetti from the centre of `origin` (an Element). Returns a handle (0 = nothing ran).
 * A second burst replaces the first rather than stacking two canvases.
 */
export function burst(origin, { count = 150 } = {}) {
    try {
        if (!origin?.getBoundingClientRect || !gfx.allows('lite')) return 0;
        stop();

        const rect = origin.getBoundingClientRect();
        const canvas = document.createElement('canvas');
        canvas.setAttribute('aria-hidden', 'true');
        Object.assign(canvas.style, {
            position: 'fixed', inset: '0', width: '100%', height: '100%',
            pointerEvents: 'none', zIndex: '60'
        });
        document.body.append(canvas);
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        canvas.width = Math.floor(window.innerWidth * dpr);
        canvas.height = Math.floor(window.innerHeight * dpr);
        const ctx = canvas.getContext('2d');
        if (!ctx) {
            canvas.remove();
            return 0;
        }
        ctx.scale(dpr, dpr);

        const { ink, fills } = tokens();
        const ox = rect.left + rect.width * 0.18;
        const oy = rect.top + rect.height / 2;
        const pieces = Array.from({ length: count }, (_, i) => {
            // Two fans: most go up and out from the row, a few shoot straight up for height.
            const angle = -Math.PI / 2 + (Math.random() - 0.5) * (i % 5 === 0 ? 0.6 : 2.4);
            const speed = 520 + Math.random() * 760;
            return {
                x: ox + (Math.random() - 0.5) * rect.width * 0.3,
                y: oy,
                vx: Math.cos(angle) * speed,
                vy: Math.sin(angle) * speed,
                w: 9 + Math.random() * 9,
                h: 7 + Math.random() * 7,
                spin: (Math.random() - 0.5) * 12,
                rot: Math.random() * Math.PI,
                flip: Math.random() * Math.PI * 2,
                flipSpeed: 5 + Math.random() * 9,
                sway: Math.random() * Math.PI * 2,
                fill: fills[i % fills.length],
                dots: Math.random() < 0.5
            };
        });

        const started = performance.now();
        let last = started;
        const instance = { canvas, stopTask: null };
        instance.stopTask = gfx.addTask('confetti', (now) => {
            const dt = Math.min(0.05, (now - last) / 1000);
            last = now;
            const t = now - started;
            if (t > DURATION_MS || !canvas.isConnected) {
                stop();
                return;
            }
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            const fade = t > DURATION_MS - 600 ? (DURATION_MS - t) / 600 : 1;
            ctx.globalAlpha = Math.max(0, fade);
            ctx.lineWidth = 1.6;
            ctx.strokeStyle = ink;
            for (const p of pieces) {
                p.vx -= p.vx * DRAG * dt;
                p.vy += (GRAVITY - p.vy * DRAG) * dt;
                p.sway += dt * 3;
                p.x += (p.vx + Math.sin(p.sway) * 40) * dt;
                p.y += p.vy * dt;
                p.rot += p.spin * dt;
                p.flip += p.flipSpeed * dt;
                if (p.y > window.innerHeight + 40) continue;

                const tilt = Math.cos(p.flip);
                ctx.save();
                ctx.translate(p.x, p.y);
                ctx.rotate(p.rot);
                ctx.scale(1, tilt || 0.01);
                ctx.fillStyle = p.fill;
                ctx.fillRect(-p.w / 2, -p.h / 2, p.w, p.h);
                if (p.dots) {
                    // A halftone corner, so the piece reads as a printed panel, not a chip.
                    ctx.fillStyle = ink;
                    for (let dx = 0; dx < 3; dx++) {
                        for (let dy = 0; dy < 2; dy++) {
                            ctx.fillRect(p.w / 2 - 3 - dx * 2.6, -p.h / 2 + 1.5 + dy * 2.6, 1, 1);
                        }
                    }
                }
                ctx.strokeRect(-p.w / 2, -p.h / 2, p.w, p.h);
                ctx.restore();
            }
        });
        active = instance;
        return 1;
    } catch {
        stop();
        return 0;
    }
}

export function stop() {
    const instance = active;
    if (!instance) return;
    active = null;
    instance.stopTask?.();
    instance.canvas.remove();
}

// ── Ink splat, for the CERTIFIED STRANGE stamp ───────────────────────────────────────────
//
// Droplets thrown off the stamp's impact in every direction. Each flies under gravity until its
// own landing moment, then stops where it is and becomes a splat — a blob with a few satellite
// specks — that fades with the rest. Mostly comic ink, a few in the accent.

const SPLAT_MS = 1500;
let activeSplat = null;

function stopSplat() {
    const instance = activeSplat;
    if (!instance) return;
    activeSplat = null;
    instance.stopTask?.();
    instance.canvas.remove();
}

/** Throws ink from viewport point (x, y). Returns 1 when it ran, 0 otherwise. */
export function splat(x, y, { count = 46, spread = 1 } = {}) {
    try {
        if (!gfx.allows('lite')) return 0;
        stopSplat();

        const canvas = document.createElement('canvas');
        canvas.setAttribute('aria-hidden', 'true');
        Object.assign(canvas.style, {
            position: 'fixed', inset: '0', width: '100%', height: '100%',
            pointerEvents: 'none', zIndex: '60'
        });
        document.body.append(canvas);
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        canvas.width = Math.floor(window.innerWidth * dpr);
        canvas.height = Math.floor(window.innerHeight * dpr);
        const ctx = canvas.getContext('2d');
        if (!ctx) {
            canvas.remove();
            return 0;
        }
        ctx.scale(dpr, dpr);

        const style = getComputedStyle(document.documentElement);
        const ink = style.getPropertyValue('--color-comic-ink').trim() || '#111111';
        const accent = style.getPropertyValue('--color-accent').trim() || '#ffcc00';
        const drops = Array.from({ length: count }, (_, i) => {
            const angle = Math.random() * Math.PI * 2;
            const speed = (260 + Math.random() * 640) * spread;
            return {
                x, y,
                vx: Math.cos(angle) * speed,
                vy: Math.sin(angle) * speed - 180,
                r: 1.5 + Math.random() * 4.5,
                land: 0.18 + Math.random() * 0.4,
                landed: false,
                specks: Array.from({ length: 3 }, () => [(Math.random() - 0.5) * 14, (Math.random() - 0.5) * 14, 0.6 + Math.random() * 1.4]),
                fill: i % 6 === 0 ? accent : ink
            };
        });

        const started = performance.now();
        let last = started;
        const instance = { canvas, stopTask: null };
        instance.stopTask = gfx.addTask('ink-splat', (now) => {
            const dt = Math.min(0.05, (now - last) / 1000);
            last = now;
            const t = (now - started) / 1000;
            if (t * 1000 > SPLAT_MS || !canvas.isConnected) {
                stopSplat();
                return;
            }
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            ctx.globalAlpha = t * 1000 > SPLAT_MS - 500 ? Math.max(0, (SPLAT_MS - t * 1000) / 500) : 1;
            for (const d of drops) {
                if (!d.landed) {
                    d.vx -= d.vx * 2 * dt;
                    d.vy += (1600 - d.vy * 2) * dt;
                    d.x += d.vx * dt;
                    d.y += d.vy * dt;
                    d.landed = t >= d.land;
                }
                ctx.fillStyle = d.fill;
                ctx.beginPath();
                if (d.landed) {
                    // Flattened on impact, with its satellites thrown around it.
                    ctx.ellipse(d.x, d.y, d.r * 1.7, d.r * 1.25, 0, 0, Math.PI * 2);
                    for (const [sx, sy, sr] of d.specks) {
                        ctx.moveTo(d.x + sx + sr, d.y + sy);
                        ctx.arc(d.x + sx, d.y + sy, sr, 0, Math.PI * 2);
                    }
                } else {
                    // Stretched along its flight, so it reads as a flung drop rather than a dot.
                    const angle = Math.atan2(d.vy, d.vx);
                    const stretch = Math.min(3, 1 + Math.hypot(d.vx, d.vy) / 500);
                    ctx.ellipse(d.x, d.y, d.r * stretch, d.r, angle, 0, Math.PI * 2);
                }
                ctx.fill();
            }
        });
        activeSplat = instance;
        return 1;
    } catch {
        stopSplat();
        return 0;
    }
}

gfx.onTierChanged((tier) => {
    if (tier === 'off') {
        stop();
        stopSplat();
    }
});
