// Liquid glass: panes that bend what is behind them, and catch a light that follows the pointer
// (or the phone's tilt).
//
// HOW THE BEND WORKS. A real lens displaces most at its rim and not at all in the middle. That
// is a displacement map: red = horizontal shift, green = vertical, 0.5 = none. This module paints
// one with a rounded-rectangle distance field (pixels near an edge are pushed inward, with a
// smooth falloff toward the middle), hands it to an SVG filter as an feImage, and the stylesheet
// applies that filter as a BACKDROP filter, ahead of the existing blur. The map is stretched to
// each pane's box, so one map serves every pane.
//
// CHROMIUM ONLY, deliberately. `backdrop-filter: url(#…)` parses in other engines and then
// renders nothing at all — a pane with no blur either — so the stylesheet keys the liquid
// version off `:root[data-glass="liquid"]`, which is set here only on a Chromium browser at the
// `full` tier. Everyone else keeps the plain frosted glass that was there before.
//
// THE LIGHT is one pair of custom properties on <html>, `--glass-lx` / `--glass-ly`, read by a
// `background-attachment: fixed` gradient on every pane — so a single light sits in the
// viewport and every pane it passes over picks it up, like one lamp over a table of glasses.
// Writes are coalesced to one per frame on the shared loop. `--glass-x` / `--glass-y` carry the
// same light as unitless 0..1 numbers for the rare comic's foil (app.css, `[data-rare]`).

import { gfx } from './gfx-core.js';

const FILTER_ID = 'posee-liquid-glass';
const MAP_SIZE = 256;
const RIM = 0.16;          // Fraction of the half-size over which the rim bends.

let filterHost = null;
let pending = null;
let flushTask = null;
let tiltListening = false;

const isChromium = () => {
    try {
        return !!navigator.userAgentData?.brands?.some((b) => /Chromium/i.test(b.brand));
    } catch {
        return false;
    }
};

/** The lens map: displacement toward the centre, strongest at the rim, zero past it. */
function paintMap() {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = MAP_SIZE;
    const ctx = canvas.getContext('2d');
    const image = ctx.createImageData(MAP_SIZE, MAP_SIZE);
    const radius = 0.22;
    for (let y = 0; y < MAP_SIZE; y++) {
        for (let x = 0; x < MAP_SIZE; x++) {
            // Centred coordinates in -1..1, then a rounded-box signed distance.
            const px = (x + 0.5) / MAP_SIZE * 2 - 1;
            const py = (y + 0.5) / MAP_SIZE * 2 - 1;
            const qx = Math.abs(px) - (1 - radius);
            const qy = Math.abs(py) - (1 - radius);
            const outside = Math.hypot(Math.max(qx, 0), Math.max(qy, 0));
            const distance = outside + Math.min(Math.max(qx, qy), 0) - radius;   // ≤ 0 inside

            // 0 deep inside, 1 at the edge, eased so the bend gathers sharply at the rim.
            const rim = Math.min(1, Math.max(0, 1 + distance / RIM));
            const strength = rim * rim * rim;
            const length = Math.hypot(px, py) || 1;
            const i = (y * MAP_SIZE + x) * 4;
            image.data[i] = 128 - (px / length) * strength * 127;
            image.data[i + 1] = 128 - (py / length) * strength * 127;
            image.data[i + 2] = 128;
            image.data[i + 3] = 255;
        }
    }
    ctx.putImageData(image, 0, 0);
    return canvas.toDataURL('image/png');
}

function ensureFilter() {
    if (filterHost?.isConnected) return true;
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('aria-hidden', 'true');
    svg.setAttribute('width', '0');
    svg.setAttribute('height', '0');
    svg.style.position = 'absolute';
    svg.innerHTML = `
        <filter id="${FILTER_ID}" x="0" y="0" width="1" height="1"
                primitiveUnits="objectBoundingBox" color-interpolation-filters="sRGB">
            <feImage href="${paintMap()}" x="0" y="0" width="1" height="1"
                     preserveAspectRatio="none" result="lens" />
            <feDisplacementMap in="SourceGraphic" in2="lens" scale="0.06"
                               xChannelSelector="R" yChannelSelector="G" />
        </filter>`;
    document.body.append(svg);
    filterHost = svg;
    return true;
}

function queueLight(x, y) {
    pending = [x, y];
    if (flushTask) return;
    flushTask = gfx.addTask('glass-light', () => {
        const value = pending;
        pending = null;
        flushTask?.();
        flushTask = null;
        if (!value) return;
        const root = document.documentElement.style;
        root.setProperty('--glass-lx', `${(value[0] * 100).toFixed(1)}%`);
        root.setProperty('--glass-ly', `${(value[1] * 100).toFixed(1)}%`);
        // The same light as plain numbers (0..1), for rules that need to calc() an angle from
        // it: the rare comic's foil tilts toward it.
        root.setProperty('--glass-x', value[0].toFixed(3));
        root.setProperty('--glass-y', value[1].toFixed(3));
    });
}

function onPointerMove(event) {
    if (event.pointerType === 'touch') return;
    queueLight(event.clientX / (window.innerWidth || 1), event.clientY / (window.innerHeight || 1));
}

/** Phone tilt: gamma is left/right, beta is front/back; ±30° covers the whole pane. */
function onTilt(event) {
    if (event.gamma === null || event.beta === null) return;
    const x = 0.5 + Math.max(-1, Math.min(1, event.gamma / 30)) * 0.5;
    const y = 0.5 + Math.max(-1, Math.min(1, (event.beta - 40) / 30)) * 0.5;
    queueLight(x, y);
}

function apply(tier) {
    try {
        const root = document.documentElement;
        const liquid = tier === 'full' && isChromium();
        if (liquid && ensureFilter()) {
            root.dataset.glass = 'liquid';
        } else {
            delete root.dataset.glass;
        }

        const lit = tier !== 'off';
        window.removeEventListener('pointermove', onPointerMove);
        if (lit) window.addEventListener('pointermove', onPointerMove, { passive: true });

        // Only where the event needs no permission prompt: iOS gates it behind
        // requestPermission, and a prompt for a highlight is not a trade worth offering.
        const tiltFree = typeof DeviceOrientationEvent !== 'undefined'
            && typeof DeviceOrientationEvent.requestPermission !== 'function';
        if (lit && tiltFree && !tiltListening) {
            window.addEventListener('deviceorientation', onTilt, { passive: true });
            tiltListening = true;
        } else if (!lit && tiltListening) {
            window.removeEventListener('deviceorientation', onTilt);
            tiltListening = false;
        }
    } catch {
        // The plain frosted glass is still there.
    }
}

export function init() {
    apply(gfx.describe().tier);
    gfx.onTierChanged(apply);
}
