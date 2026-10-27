// Map view for discovery and the Hall of Fame (fronted by MapView.razor).
//
// MapLibre GL is ~280KB gzipped, so it is fetched the first time a map opens and never on a page
// without one. The 5.x UMD build, not 6.x: 6.x ships only ES modules with a separate worker
// file, and a module worker cannot be started from another origin. 5.x inlines its worker.
// Both files are pinned by SRI.
//
// Tiles are OpenFreeMap: free, no key, no per-load billing (the Google Maps JS API bills
// every map load).
//
// Pins are real <button>s rather than a GL symbol layer: there are at most a few dozen, and a
// button is focusable, labelled and styleable from app.css tokens. MapLibre positions a marker
// with `transform`, so pin styles must never set one on `.map-pin` itself — the face inside
// carries the hover and selection scale.
//
// Under the pins, every drawn place glows with its strangeness (see "Strangeness heat" below),
// and tapping a pin flies the camera to it.
//
// Nothing here may throw into .NET: every export catches and returns a benign value, and a 0
// handle means "no map" (MapView then offers the list instead).

import { gfx, prefersDark } from './gfx-core.js';
import { loadScript, loadLayeredStyle } from './vendor.js';

const MAPLIBRE = 'https://cdn.jsdelivr.net/npm/maplibre-gl@5.24.0/dist/';
const SCRIPT_SRI = 'sha384-5+cfbwT0iiub6VsQAdn6yz16nr6sDiQoHx6tm4O8OVYXHYOxcffFmCJBL0dgdvGp';
const STYLES_SRI = 'sha384-uTttxo/aOKbdE5RlD/SPzSDoDmNvGlUYPjONi2MN/b7c9HPSvW07OIuyP7uL6jxK';
const TILE_STYLES = {
    light: 'https://tiles.openfreemap.org/styles/positron',
    dark: 'https://tiles.openfreemap.org/styles/dark'
};

const maps = new Map();
let nextHandle = 1;
let library = null;

const reducedMotion = () => matchMedia('(prefers-reduced-motion: reduce)').matches;

const tileStyle = () => (prefersDark() ? TILE_STYLES.dark : TILE_STYLES.light);

function loadLibrary() {
    library ??= Promise.all([
        loadScript(`${MAPLIBRE}maplibre-gl.js`, { integrity: SCRIPT_SRI, ready: () => !!window.maplibregl }),
        loadLayeredStyle('maplibre', `${MAPLIBRE}maplibre-gl.css`, { init: { integrity: STYLES_SRI, mode: 'cors' } })
    ])
        .then(() => window.maplibregl)
        .catch((error) => {
            library = null; // a flaky network gets another try on the next open
            throw error;
        });
    return library;
}

function band(score) {
    if (score === null || score === undefined) return 'new';
    if (score >= 60) return 'high';
    if (score >= 30) return 'mid';
    return 'low';
}

// Places in one building (suites of one address) sit metres apart, closer than any zoom
// separates, and the top pin would hide the rest from a pointer. Pins within ~30m of each other
// are nudged onto a small ring around their spot.
const NEIGHBOUR_METRES = 30;

function metresApart(a, b) {
    const dLat = (a.lat - b.lat) * 111_320;
    const dLng = (a.lng - b.lng) * 111_320 * Math.cos((a.lat * Math.PI) / 180);
    return Math.hypot(dLat, dLng);
}

function spread(pins) {
    const groups = [];
    for (const pin of pins) {
        const group = groups.find((g) => metresApart(g[0], pin) < NEIGHBOUR_METRES);
        if (group) group.push(pin);
        else groups.push([pin]);
    }
    const offsets = new Map();
    for (const group of groups) {
        group.forEach((pin, i) => {
            if (group.length === 1) return offsets.set(pin.id, [0, 0]);
            const angle = (2 * Math.PI * i) / group.length - Math.PI / 2;
            const radius = 14 + 4 * group.length;
            offsets.set(pin.id, [Math.round(radius * Math.cos(angle)), Math.round(radius * Math.sin(angle))]);
        });
    }
    return offsets;
}

function renderPins(entry, pins) {
    for (const { marker } of entry.markers.values()) marker.remove();
    entry.markers.clear();
    const offsets = spread(pins);

    for (const pin of pins) {
        const el = document.createElement('button');
        el.type = 'button';
        el.className = 'map-pin';
        el.dataset.band = band(pin.score);
        el.setAttribute('aria-label', pin.label);
        el.title = pin.name;

        const face = document.createElement('span');
        face.className = 'map-pin-face';
        face.setAttribute('aria-hidden', 'true');
        face.textContent = pin.score ?? '';
        el.append(face);

        el.addEventListener('click', (event) => {
            event.stopPropagation();
            markSelected(entry, pin.id);
            flyToPin(entry, pin, el, event);
            entry.dotnet.invokeMethodAsync('SelectPin', pin.id).catch(() => {});
        });

        const marker = new window.maplibregl.Marker({ element: el, anchor: 'bottom', offset: offsets.get(pin.id) })
            .setLngLat([pin.lng, pin.lat])
            .addTo(entry.map);
        entry.markers.set(pin.id, { marker, el, face });
    }
    dropPins(entry, pins);
}

// New results drop in nearest-first (from "you are here", else the middle of the results), and
// the first few land with a pluck panned to where they fell. The face falls, never the button:
// MapLibre owns the button's transform. The fall itself is `map-pin-drop` in app.css.
const DROP_STAGGER_MS = 40;
const DROP_MAX_MS = 900;
const DROP_LAND_MS = 690;   // the 350ms lead plus the first touchdown in map-pin-drop
const VOICED_PINS = 8;

function dropPins(entry, pins) {
    if (!gfx.allows('full') || reducedMotion() || pins.length === 0) return;
    const centre = entry.origin?.getLngLat() ?? {
        lat: pins.reduce((sum, p) => sum + p.lat, 0) / pins.length,
        lng: pins.reduce((sum, p) => sum + p.lng, 0) / pins.length
    };
    [...pins]
        .sort((a, b) => metresApart(centre, a) - metresApart(centre, b))
        .forEach((pin, rank) => {
            const face = entry.markers.get(pin.id)?.face;
            if (!face) return;
            const delay = Math.min(DROP_MAX_MS, rank * DROP_STAGGER_MS);
            face.style.setProperty('--drop-delay', `${delay}ms`);
            face.dataset.drop = '';
            face.addEventListener('animationend', () => delete face.dataset.drop, { once: true });
            if (rank >= VOICED_PINS) return;
            setTimeout(() => {
                if (!face.isConnected) return;
                const rect = face.getBoundingClientRect();
                window.poseeFx?.playPinDrop?.(rect.left + rect.width / 2, rank, pin.score);
            }, delay + DROP_LAND_MS);
        });
}

// A tap brings the place to the middle, at street level if the view was wider. The flight is a
// code move (no originalEvent), so it never offers "search this area". Reduced motion jumps.
// Keyboard activation has no pointer position, so the whoosh pans to the pin's own centre.
function flyToPin(entry, pin, el, event) {
    try {
        const center = [pin.lng, pin.lat];
        const zoom = Math.max(entry.map.getZoom(), 14);
        if (reducedMotion()) {
            entry.map.jumpTo({ center, zoom });
            return;
        }
        const rect = el.getBoundingClientRect();
        window.poseeFx?.playMapFly?.(event.clientX || rect.left + rect.width / 2);
        entry.map.flyTo({ center, zoom, curve: 1.6, speed: 1.3, essential: true });
    } catch {
        // The selection already happened; a camera that cannot move is not an error.
    }
}

function markSelected(entry, id) {
    entry.selected = id ?? null;
    for (const [pinId, { el }] of entry.markers) {
        const on = pinId === entry.selected;
        el.setAttribute('aria-pressed', on ? 'true' : 'false');
        el.toggleAttribute('data-selected', on);
    }
}

// The "you are here" dot: informative, not interactive, so an image with a label rather than a
// button. Replaced, never duplicated, when the origin moves.
function placeOrigin(entry, lat, lng) {
    entry.origin?.remove();
    entry.origin = null;
    if (lat === null || lat === undefined || lng === null || lng === undefined) return;
    const el = document.createElement('div');
    el.className = 'map-origin';
    el.setAttribute('role', 'img');
    el.setAttribute('aria-label', 'Your location');
    entry.origin = new window.maplibregl.Marker({ element: el }).setLngLat([lng, lat]).addTo(entry.map);
}

function frame(entry, pins) {
    const points = pins.map((p) => [p.lng, p.lat]);
    if (entry.origin) {
        const { lng, lat } = entry.origin.getLngLat();
        points.push([lng, lat]);
    }
    if (points.length === 0) return;
    if (points.length === 1) {
        entry.map.jumpTo({ center: points[0], zoom: 14 });
        return;
    }
    const bounds = new window.maplibregl.LngLatBounds();
    for (const point of points) bounds.extend(point);
    entry.map.fitBounds(bounds, { padding: 56, maxZoom: 15, animate: !reducedMotion() });
}

// Only a move the user made is reported ("search this area" offers itself after one). Moves
// the code makes — fitBounds, jumpTo — carry no originalEvent; drags, wheel, keyboard and the
// zoom buttons all do.
function watchUserMoves(entry) {
    entry.map.on('movestart', (event) => {
        if (event.originalEvent) entry.userMoving = true;
    });
    // Map sounds: the camera's speed while the user moves it, in CSS px per second (a zoom step
    // counts as 400px). The audio side ignores it unless map sounds are on.
    entry.map.on('move', (event) => {
        if (!event.originalEvent) {
            entry.lastMove = null;
            return;
        }
        const now = performance.now();
        const previous = entry.lastMove;
        entry.lastMove = { centre: entry.map.getCenter(), zoom: entry.map.getZoom(), at: now };
        if (!previous || now - previous.at > 200 || now === previous.at) return;
        const canvas = entry.map.getCanvas();
        const was = entry.map.project(previous.centre);
        const travelled = Math.hypot(was.x - canvas.clientWidth / 2, was.y - canvas.clientHeight / 2)
            + Math.abs(entry.lastMove.zoom - previous.zoom) * 400;
        window.poseeFx?.mapWind?.(travelled / ((now - previous.at) / 1000));
    });
    entry.map.on('moveend', () => {
        if (!entry.userMoving) return;
        entry.userMoving = false;
        const { lat, lng } = entry.map.getCenter();
        entry.dotnet.invokeMethodAsync('AreaMoved', lat, lng, entry.map.getZoom()).catch(() => {});
    });
}

// ── Strangeness heat ─────────────────────────────────────────────────────────────────────
//
// Every drawn place gets a soft gaussian glow, sized and coloured by its score, so a strange
// neighbourhood reads at a glance and neighbouring glows pool into one. It is a MapLibre custom
// layer drawing into MapLibre's own WebGL context — a second context (or a canvas over the map)
// would cost another framebuffer and could not sit beneath the labels. One instanced quad per
// place; with a few dozen places the clip positions are projected on the CPU in float64 each
// frame, which keeps glows from shimmering at street zoom the way float32 world matrices do.
//
// Dark tiles: additive light, so overlaps brighten. Light (Positron) tiles: additive light is
// invisible on near-white, so the glow multiplies a tint in instead and overlaps deepen.
//
// It breathes only at the `full` tier, only while the map is on screen and the tab visible: a
// gfx task asks MapLibre for a repaint at ~30fps. At `lite` and `off` it is drawn static and
// costs nothing between the map's own repaints. MapLibre 5 falls back to WebGL1 on old devices;
// there the heat is simply absent.

const HEAT_LAYER = 'posee-heat';
const PULSE_INTERVAL_MS = 33;

const HEAT_VERTEX_SHADER = `#version 300 es
uniform vec2 u_viewport;      // drawing-buffer pixels
uniform float u_scale;        // device px per CSS px, times the zoom factor
uniform float u_time;         // seconds
uniform float u_pulse;        // 0 static, 1 breathing
layout(location = 0) in vec2 a_corner;
layout(location = 1) in vec4 a_clip;
layout(location = 2) in vec4 a_color;   // rgb + strength
layout(location = 3) in vec3 a_motion;  // radius (CSS px), phase, rate (rad/s)
out vec2 v_uv;
out vec4 v_color;
void main() {
    float wave = sin(u_time * a_motion.z + a_motion.y) * u_pulse;
    float radius = a_motion.x * u_scale * (1.0 + 0.12 * a_color.a * wave);
    v_uv = a_corner;
    v_color = vec4(a_color.rgb, a_color.a * (1.0 + 0.25 * wave));
    gl_Position = a_clip + vec4(a_corner * radius * 2.0 / u_viewport * a_clip.w, 0.0, 0.0);
}`;

const HEAT_FRAGMENT_SHADER = `#version 300 es
precision mediump float;
uniform float u_multiply;
in vec2 v_uv;
in vec4 v_color;
out vec4 fragColor;
void main() {
    float d2 = dot(v_uv, v_uv);
    if (d2 >= 1.0) discard;
    // A gaussian eased to exactly zero at the quad's rim, so no square edge ever shows.
    float k = clamp(exp(-d2 * 3.2) * (1.0 - d2) * v_color.a, 0.0, 1.0);
    fragColor = u_multiply > 0.5
        ? vec4(mix(vec3(1.0), v_color.rgb, k), 1.0)
        : vec4(v_color.rgb * k, 0.0);
}`;

const CORNERS = new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]);

function hexToRgb(value, fallback) {
    const hex = /^#?([0-9a-f]{6})$/i.exec(value.trim())?.[1];
    if (!hex) return fallback;
    const n = parseInt(hex, 16);
    return [(n >> 16) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
}

// Read on every (re)add, which follows every theme flip: the tokens change with the theme.
function heatPalette() {
    const css = getComputedStyle(document.documentElement);
    const token = (name, fallback) => hexToRgb(css.getPropertyValue(name), fallback);
    return {
        dark: prefersDark(),
        low: token('--color-brand', [0.49, 0.23, 0.93]),
        mid: token('--color-accent', [0.96, 0.62, 0.04]),
        high: token('--color-danger', [0.84, 0.15, 0.21])
    };
}

const mix3 = (a, b, t) => a.map((v, i) => v + (b[i] - v) * t);

// A ramp rather than three hard bands: brand at ordinary, accent around the middle, danger
// for the truly strange.
function heatColour(palette, score) {
    const t = Math.min(1, Math.max(0, score / 100));
    return t < 0.5 ? mix3(palette.low, palette.mid, t / 0.5) : mix3(palette.mid, palette.high, (t - 0.5) / 0.5);
}

// Per-place phase, so neighbouring glows breathe out of step. Seeded by id, like the audio motif.
function phaseOf(id) {
    let h = 2166136261;
    for (let i = 0; i < id.length; i++) h = Math.imul(h ^ id.charCodeAt(i), 16777619);
    return ((h >>> 0) / 4294967296) * Math.PI * 2;
}

function createHeatResources(gl) {
    const compile = (type, source) => {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader) ?? 'heat shader');
        return shader;
    };
    const program = gl.createProgram();
    gl.attachShader(program, compile(gl.VERTEX_SHADER, HEAT_VERTEX_SHADER));
    gl.attachShader(program, compile(gl.FRAGMENT_SHADER, HEAT_FRAGMENT_SHADER));
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) ?? 'heat link');

    const previousVao = gl.getParameter(gl.VERTEX_ARRAY_BINDING);
    const previousBuffer = gl.getParameter(gl.ARRAY_BUFFER_BINDING);
    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);

    const corners = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, corners);
    gl.bufferData(gl.ARRAY_BUFFER, CORNERS, gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);

    const clip = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, clip);
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 4, gl.FLOAT, false, 0, 0);
    gl.vertexAttribDivisor(1, 1);

    const attributes = gl.createBuffer(); // rgb, strength, radius, phase, rate
    gl.bindBuffer(gl.ARRAY_BUFFER, attributes);
    gl.enableVertexAttribArray(2);
    gl.vertexAttribPointer(2, 4, gl.FLOAT, false, 28, 0);
    gl.vertexAttribDivisor(2, 1);
    gl.enableVertexAttribArray(3);
    gl.vertexAttribPointer(3, 3, gl.FLOAT, false, 28, 16);
    gl.vertexAttribDivisor(3, 1);

    gl.bindVertexArray(previousVao);
    gl.bindBuffer(gl.ARRAY_BUFFER, previousBuffer);

    const at = (name) => gl.getUniformLocation(program, name);
    return {
        gl, program, vao, clip, attributes, clipData: new Float32Array(0), uploaded: -1,
        u: { viewport: at('u_viewport'), scale: at('u_scale'), time: at('u_time'), pulse: at('u_pulse'), multiply: at('u_multiply') }
    };
}

function drawHeat(entry, gl, args) {
    const heat = entry.heat;
    const count = heat.places.length;
    if (count === 0 || heat.broken || !heat.palette || gl.isContextLost()) return;
    if (typeof WebGL2RenderingContext === 'undefined' || !(gl instanceof WebGL2RenderingContext)) return;

    let res = heat.res;
    if (!res || res.gl !== gl) {
        try {
            res = heat.res = createHeatResources(gl);
        } catch (error) {
            heat.broken = true; // never retried per frame; the pins still work without it
            console.debug('[map-view] heat unavailable', error);
            syncPulse(entry);
            return;
        }
    }

    const saved = {
        program: gl.getParameter(gl.CURRENT_PROGRAM),
        vao: gl.getParameter(gl.VERTEX_ARRAY_BINDING),
        buffer: gl.getParameter(gl.ARRAY_BUFFER_BINDING),
        blend: gl.isEnabled(gl.BLEND),
        srcRgb: gl.getParameter(gl.BLEND_SRC_RGB),
        dstRgb: gl.getParameter(gl.BLEND_DST_RGB),
        srcAlpha: gl.getParameter(gl.BLEND_SRC_ALPHA),
        dstAlpha: gl.getParameter(gl.BLEND_DST_ALPHA)
    };

    try {
        gl.useProgram(res.program);
        gl.bindVertexArray(res.vao);

        if (res.uploaded !== heat.version) {
            gl.bindBuffer(gl.ARRAY_BUFFER, res.attributes);
            gl.bufferData(gl.ARRAY_BUFFER, heat.attributeData, gl.STATIC_DRAW);
            res.uploaded = heat.version;
        }

        // Project each place to clip space in float64 (see the section header).
        const m = args.defaultProjectionData.mainMatrix;
        if (res.clipData.length !== count * 4) res.clipData = new Float32Array(count * 4);
        const out = res.clipData;
        heat.places.forEach(({ x, y }, i) => {
            const w = m[3] * x + m[7] * y + m[15];
            const o = i * 4;
            if (w <= 0) {
                out.set([4, 4, 0, 1], o); // behind a pitched camera: off-screen
                return;
            }
            out[o] = m[0] * x + m[4] * y + m[12];
            out[o + 1] = m[1] * x + m[5] * y + m[13];
            out[o + 2] = m[2] * x + m[6] * y + m[14];
            out[o + 3] = w;
        });
        gl.bindBuffer(gl.ARRAY_BUFFER, res.clip);
        gl.bufferData(gl.ARRAY_BUFFER, out, gl.DYNAMIC_DRAW);

        const cssWidth = entry.map.getCanvas().clientWidth || gl.drawingBufferWidth;
        const dpr = gl.drawingBufferWidth / cssWidth;
        // 40–140 CSS px at zoom 13; smaller when zoomed out over a whole city, larger at street level.
        const zoomFactor = Math.min(1.35, Math.max(0.5, 2 ** ((entry.map.getZoom() - 13) * 0.4)));
        gl.uniform2f(res.u.viewport, gl.drawingBufferWidth, gl.drawingBufferHeight);
        gl.uniform1f(res.u.scale, dpr * zoomFactor);
        gl.uniform1f(res.u.time, (performance.now() / 1000) % 3600);
        gl.uniform1f(res.u.pulse, heat.stopPulse ? 1 : 0);
        gl.uniform1f(res.u.multiply, heat.palette.dark ? 0 : 1);

        gl.enable(gl.BLEND);
        if (heat.palette.dark) gl.blendFuncSeparate(gl.ONE, gl.ONE, gl.ZERO, gl.ONE);
        else gl.blendFuncSeparate(gl.DST_COLOR, gl.ZERO, gl.ZERO, gl.ONE);
        gl.drawArraysInstanced(gl.TRIANGLES, 0, 6, count);
    } finally {
        // MapLibre marks its state cache dirty after a custom layer, but leave it as found anyway.
        gl.bindVertexArray(saved.vao);
        gl.bindBuffer(gl.ARRAY_BUFFER, saved.buffer);
        gl.useProgram(saved.program);
        gl.blendFuncSeparate(saved.srcRgb, saved.dstRgb, saved.srcAlpha, saved.dstAlpha);
        if (!saved.blend) gl.disable(gl.BLEND);
    }
}

// Places with a score only: an undrawn place has nothing to glow about.
function setHeatPins(entry, pins) {
    const heat = entry.heat;
    const scored = pins.filter((p) => typeof p.score === 'number');
    heat.places = scored.map((p) => window.maplibregl.MercatorCoordinate.fromLngLat([p.lng, p.lat]));
    heat.scored = scored;
    fillHeatAttributes(heat);
    syncPulse(entry);
    syncHum(entry);
    entry.map.triggerRepaint();
}

// Map sounds: the glow, heard — while the map is on screen and has drawn places.
function syncHum(entry) {
    const { scored, onScreen } = entry.heat;
    if (onScreen && scored.length > 0) {
        const mean = scored.reduce((sum, p) => sum + p.score, 0) / scored.length;
        window.poseeFx?.mapHum?.(scored.length, mean);
    } else {
        window.poseeFx?.mapHumStop?.();
    }
}

function fillHeatAttributes(heat) {
    if (!heat.palette) return;
    const data = new Float32Array(heat.scored.length * 7);
    heat.scored.forEach((p, i) => {
        const s = Math.min(100, Math.max(0, p.score)) / 100;
        const [r, g, b] = heatColour(heat.palette, p.score);
        // Multiply needs less strength than light does to read at the same weight.
        const strength = (heat.palette.dark ? 0.4 + 0.6 * s : 0.3 + 0.45 * s);
        data.set([r, g, b, strength, 40 + 100 * s, phaseOf(p.id), 0.9 + 1.7 * s], i * 7);
    });
    heat.attributeData = data;
    heat.version++;
}

// The style has just (re)loaded — first load, or a theme flip, which drops every custom layer.
// Beneath the first symbol layer, so street and place names stay legible over a glow.
function addHeatLayer(entry) {
    try {
        const { map, heat } = entry;
        heat.palette = heatPalette();
        fillHeatAttributes(heat);
        if (map.getLayer(HEAT_LAYER)) return;
        // Just above the last ground fill (land use, buildings), so roads and every label draw
        // crisply over the glow. Not "before the first symbol layer": OpenFreeMap puts water
        // names beneath the land-use fills, which would clip the glow into hard polygons.
        const layers = map.getStyle()?.layers ?? [];
        const lastFill = layers.findLastIndex((layer) => layer.type === 'fill' || layer.type === 'background');
        const beforeId = layers[lastFill + 1]?.id;
        map.addLayer({
            id: HEAT_LAYER,
            type: 'custom',
            renderingMode: '2d',
            render: (gl, args) => drawHeat(entry, gl, args)
        }, beforeId);
    } catch (error) {
        console.debug('[map-view] heat layer not added', error);
    }
}

function syncPulse(entry) {
    const heat = entry.heat;
    const wanted = heat.onScreen && heat.places.length > 0 && !heat.broken && gfx.allows('full');
    if (wanted && !heat.stopPulse) {
        let last = 0;
        heat.stopPulse = gfx.addTask('map-heat', (now) => {
            if (now - last < PULSE_INTERVAL_MS) return;
            last = now;
            if (entry.map.getContainer().isConnected) entry.map.triggerRepaint();
        });
    } else if (!wanted && heat.stopPulse) {
        heat.stopPulse();
        heat.stopPulse = null;
        entry.map.triggerRepaint(); // settle on the static glow
    }
}

function startHeat(entry, host, pins) {
    const heat = {
        places: [], scored: [], attributeData: new Float32Array(0), version: 0,
        palette: null, res: null, broken: false, onScreen: true, stopPulse: null
    };
    entry.heat = heat;
    const { map } = entry;
    map.on('style.load', () => addHeatLayer(entry));
    map.on('webglcontextlost', () => { heat.res = null; });

    const visibility = new IntersectionObserver((records) => {
        heat.onScreen = records[records.length - 1].isIntersecting;
        syncPulse(entry);
        syncHum(entry);
    });
    visibility.observe(host);
    const stopTier = gfx.onTierChanged(() => {
        syncPulse(entry);
        map.triggerRepaint();
    });
    heat.teardown = () => {
        visibility.disconnect();
        stopTier();
        heat.stopPulse?.();
        heat.stopPulse = null;
        window.poseeFx?.mapHumStop?.();
    };
    setHeatPins(entry, pins);
}

export async function mount(host, pinsJson, dotnet, selectedId, originLat, originLng) {
    try {
        const maplibregl = await loadLibrary();
        if (!host?.isConnected) return 0;

        const pins = JSON.parse(pinsJson);
        const map = new maplibregl.Map({
            container: host,
            style: tileStyle(),
            center: [0, 20],
            zoom: 1,
            attributionControl: { compact: true },
            // The map sits in a scrolling page: one finger (or a bare wheel) scrolls the page,
            // two fingers (or ctrl+wheel) move the map.
            cooperativeGestures: true,
            fadeDuration: reducedMotion() ? 0 : 300
        });
        map.addControl(new maplibregl.NavigationControl({ showCompass: false }), 'top-right');

        const entry = { map, dotnet, markers: new Map(), selected: null, origin: null, style: tileStyle() };
        const onTheme = () => {
            const next = tileStyle();
            if (next === entry.style) return;
            entry.style = next;
            map.setStyle(next, { diff: false });
        };
        const scheme = matchMedia('(prefers-color-scheme: dark)');
        const themeObserver = new MutationObserver(onTheme);
        themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
        scheme.addEventListener('change', onTheme);
        entry.teardown = () => {
            themeObserver.disconnect();
            scheme.removeEventListener('change', onTheme);
            entry.heat?.teardown();
        };

        placeOrigin(entry, originLat, originLng);
        renderPins(entry, pins);
        startHeat(entry, host, pins);
        markSelected(entry, selectedId);
        frame(entry, pins);
        watchUserMoves(entry);

        const handle = nextHandle++;
        maps.set(handle, entry);
        return handle;
    } catch (error) {
        console.debug('[map-view] unavailable', error);
        return 0;
    }
}

// reframe=false keeps the camera where the user put it: new pins from "search this area" land
// in the view they were searched from.
export function update(handle, pinsJson, selectedId, pinsChanged, reframe, originLat, originLng) {
    const entry = maps.get(handle);
    if (!entry) return false;
    try {
        if (pinsChanged) {
            const pins = JSON.parse(pinsJson);
            placeOrigin(entry, originLat, originLng);
            renderPins(entry, pins);
            setHeatPins(entry, pins);
            if (reframe) frame(entry, pins);
        }
        markSelected(entry, selectedId);
        return true;
    } catch (error) {
        console.debug('[map-view] update failed', error);
        return false;
    }
}

export function unmount(handle) {
    const entry = maps.get(handle);
    if (!entry) return;
    maps.delete(handle);
    try {
        entry.teardown?.();
        entry.map.remove();
    } catch {
        // Already gone with its container.
    }
}
