// Ink motes: a few thousand specks drifting through a curl-noise current behind the comic page,
// thicker and faster the stranger the comic, parting around the pointer.
//
// WebGPU, because this is the one effect in the app where the work is per-particle simulation:
// a compute pass advances every mote on the GPU and a render pass draws them as instanced quads
// straight out of the same storage buffer, so nothing round-trips through JavaScript per frame.
// There is no WebGL fallback on purpose — without WebGPU the switch is not offered at all.
//
// Opt-in (`posee_gpu_motes`, the "Ink motes" switch on /settings), `full` tier only, one
// instance at a time, ticked by the shared loop. A tier drop or a lost device stops it; the
// comic page asks again next time it opens.

import { gfx } from './gfx-core.js';

const STORAGE_KEY = 'posee_gpu_motes';
const WORKGROUP = 64;

const COMPUTE_WGSL = /* wgsl */`
struct Mote { pos: vec2f, vel: vec2f };
struct Params {
    time: f32, dt: f32, strange: f32, aspect: f32,
    pointer: vec2f, count: u32, size: f32,
    color: vec4f,
    resolution: vec2f, pad: vec2f,
};
@group(0) @binding(0) var<storage, read_write> motes: array<Mote>;
@group(0) @binding(1) var<uniform> params: Params;

fn hash(p: vec2f) -> f32 {
    var q = fract(p * vec2f(123.34, 456.21));
    q += dot(q, q + 45.32);
    return fract(q.x * q.y);
}

fn noise(p: vec2f) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2f(1.0, 0.0)), u.x),
               mix(hash(i + vec2f(0.0, 1.0)), hash(i + vec2f(1.0, 1.0)), u.x), u.y);
}

// Divergence-free flow from the noise's gradient turned 90 degrees: motes swirl, never pile up.
fn curl(p: vec2f) -> vec2f {
    let e = 0.01;
    let dx = noise(p + vec2f(e, 0.0)) - noise(p - vec2f(e, 0.0));
    let dy = noise(p + vec2f(0.0, e)) - noise(p - vec2f(0.0, e));
    return vec2f(dy, -dx) / (2.0 * e);
}

@compute @workgroup_size(${WORKGROUP})
fn advance(@builtin(global_invocation_id) id: vec3u) {
    let i = id.x;
    if (i >= params.count) { return; }
    var m = motes[i];
    let scale = vec2f(params.aspect, 1.0);
    let drift = params.time * (0.04 + params.strange * 0.2);
    var acc = curl(m.pos * scale * (1.6 + params.strange * 2.4) + drift) * (0.25 + params.strange * 0.9);

    // Parted by the pointer, as if it were a finger drawn through the ink.
    let away = (m.pos - params.pointer) * scale;
    let r2 = dot(away, away);
    acc += away / sqrt(r2 + 1e-5) * 0.04 / (r2 + 0.004);

    m.vel = m.vel * exp(-2.5 * params.dt) + acc * params.dt;
    m.pos = fract(m.pos + m.vel * params.dt * 0.08 + 1.0);
    motes[i] = m;
}`;

const RENDER_WGSL = /* wgsl */`
struct Mote { pos: vec2f, vel: vec2f };
struct Params {
    time: f32, dt: f32, strange: f32, aspect: f32,
    pointer: vec2f, count: u32, size: f32,
    color: vec4f,
    resolution: vec2f, pad: vec2f,
};
@group(0) @binding(0) var<storage, read> motes: array<Mote>;
@group(0) @binding(1) var<uniform> params: Params;

struct Out { @builtin(position) position: vec4f, @location(0) uv: vec2f, @location(1) alpha: f32 };

@vertex
fn vs(@builtin(vertex_index) v: u32, @builtin(instance_index) i: u32) -> Out {
    var corners = array<vec2f, 6>(vec2f(-1.0, -1.0), vec2f(1.0, -1.0), vec2f(-1.0, 1.0),
                                  vec2f(-1.0, 1.0), vec2f(1.0, -1.0), vec2f(1.0, 1.0));
    let corner = corners[v];
    let m = motes[i];
    // A per-mote size and brightness, fixed by its index.
    let seed = fract(sin(f32(i) * 12.9898) * 43758.5453);
    let radius = params.size * (0.6 + seed * 1.4);
    let ndc = vec2f(m.pos.x * 2.0 - 1.0, 1.0 - m.pos.y * 2.0);
    var out: Out;
    out.position = vec4f(ndc + corner * radius * 2.0 / params.resolution, 0.0, 1.0);
    out.uv = corner;
    out.alpha = 0.25 + seed * 0.55;
    return out;
}

@fragment
fn fs(frag: Out) -> @location(0) vec4f {
    let d2 = dot(frag.uv, frag.uv);
    if (d2 > 1.0) { discard; }
    let a = params.color.a * frag.alpha * (1.0 - d2);
    return vec4f(params.color.rgb * a, a);   // premultiplied
}`;

let instance = null;
let nextHandle = 1;

const readEnabled = () => {
    try {
        return localStorage.getItem(STORAGE_KEY) === 'true';
    } catch {
        return false;
    }
};

export const supported = () => typeof navigator !== 'undefined' && !!navigator.gpu;

export function describe() {
    return { supported: supported(), enabled: supported() && readEnabled(), explicit: readEnabled() };
}

export function setEnabled(enabled) {
    try {
        localStorage.setItem(STORAGE_KEY, String(!!enabled));
    } catch {
        // Session-only preference.
    }
    if (!enabled) stop(instance?.handle);
    return supported() && !!enabled;
}

function readColor() {
    const hex = /^#?([0-9a-f]{6})$/i.exec(getComputedStyle(document.documentElement).getPropertyValue('--color-brand').trim())?.[1];
    const n = hex ? parseInt(hex, 16) : 0x7c3aed;
    return [(n >> 16) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255, 0.45];
}

/** Returns a handle at once (0 = not allowed here); the GPU comes up asynchronously behind it. */
export function start(score) {
    if (!supported() || !readEnabled() || !gfx.allows('full')) return 0;
    stop(instance?.handle);

    const strange = Math.min(1, Math.max(0, (score ?? 40) / 100));
    const canvas = document.createElement('canvas');
    canvas.className = 'fx-motes';
    canvas.setAttribute('aria-hidden', 'true');
    // Right after the backdrop, in its stacking context: over the field, under every page.
    const backdrop = document.querySelector('.fx-backdrop');
    if (backdrop) backdrop.after(canvas);
    else document.body.append(canvas);

    const self = {
        handle: nextHandle++,
        canvas,
        strange,
        count: Math.round(1200 + strange * 3600),
        pointer: [0.5, 0.5],
        color: readColor(),
        stopped: false,
        device: null,
        task: null
    };
    instance = self;
    const onPointer = (event) => {
        self.pointer = [event.clientX / (window.innerWidth || 1), event.clientY / (window.innerHeight || 1)];
    };
    const onTheme = () => { self.color = readColor(); };
    const themeObserver = new MutationObserver(onTheme);
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    window.addEventListener('pointermove', onPointer, { passive: true });
    self.cleanup = () => {
        themeObserver.disconnect();
        window.removeEventListener('pointermove', onPointer);
    };

    boot(self).catch((error) => {
        console.warn('[ink-motes] WebGPU unavailable', error);
        stop(self.handle);
    });
    return self.handle;
}

async function boot(self) {
    const adapter = await navigator.gpu.requestAdapter({ powerPreference: 'low-power' });
    if (!adapter) throw new Error('no adapter');
    const device = await adapter.requestDevice();
    if (self.stopped) {
        device.destroy();
        return;
    }
    self.device = device;
    device.lost.then(() => stop(self.handle));
    // Validation errors never throw; without this a broken pipeline is an invisible loop.
    device.addEventListener('uncapturederror', (event) => {
        console.warn('[ink-motes] GPU error; stopping', event.error?.message);
        stop(self.handle);
    });

    const context = self.canvas.getContext('webgpu');
    const format = navigator.gpu.getPreferredCanvasFormat();
    context.configure({ device, format, alphaMode: 'premultiplied' });

    const seed = new Float32Array(self.count * 4);
    for (let i = 0; i < self.count; i++) {
        seed[i * 4] = Math.random();
        seed[i * 4 + 1] = Math.random();
    }
    const motes = device.createBuffer({ size: seed.byteLength, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
    device.queue.writeBuffer(motes, 0, seed);

    const paramsData = new ArrayBuffer(64);
    const f32 = new Float32Array(paramsData);
    const u32 = new Uint32Array(paramsData);
    const params = device.createBuffer({ size: 64, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });

    const compute = device.createComputePipeline({
        layout: 'auto',
        compute: { module: device.createShaderModule({ code: COMPUTE_WGSL }), entryPoint: 'advance' }
    });
    const renderModule = device.createShaderModule({ code: RENDER_WGSL });
    const render = device.createRenderPipeline({
        layout: 'auto',
        vertex: { module: renderModule, entryPoint: 'vs' },
        fragment: {
            module: renderModule,
            entryPoint: 'fs',
            targets: [{
                format,
                blend: {
                    color: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha' },
                    alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha' }
                }
            }]
        },
        primitive: { topology: 'triangle-list' }
    });
    const bind = (pipeline) => device.createBindGroup({
        layout: pipeline.getBindGroupLayout(0),
        entries: [{ binding: 0, resource: { buffer: motes } }, { binding: 1, resource: { buffer: params } }]
    });
    const computeGroup = bind(compute);
    const renderGroup = bind(render);
    if (self.stopped) {
        device.destroy();
        return;
    }

    const started = performance.now();
    let last = started;
    self.task = gfx.addTask('ink-motes', (now) => {
        const dpr = Math.min(window.devicePixelRatio || 1, 1.5);
        const width = Math.max(1, Math.floor(self.canvas.clientWidth * dpr));
        const height = Math.max(1, Math.floor(self.canvas.clientHeight * dpr));
        if (self.canvas.width !== width || self.canvas.height !== height) {
            self.canvas.width = width;
            self.canvas.height = height;
        }
        const dt = Math.min(0.05, (now - last) / 1000);
        last = now;

        f32.set([(now - started) / 1000, dt, self.strange, width / height, self.pointer[0], self.pointer[1]], 0);
        u32[6] = self.count;
        f32[7] = (1.0 + self.strange * 1.2) * dpr;
        f32.set(self.color, 8);
        f32.set([width, height], 12);
        device.queue.writeBuffer(params, 0, paramsData);

        const encoder = device.createCommandEncoder();
        const pass = encoder.beginComputePass();
        pass.setPipeline(compute);
        pass.setBindGroup(0, computeGroup);
        pass.dispatchWorkgroups(Math.ceil(self.count / WORKGROUP));
        pass.end();
        const draw = encoder.beginRenderPass({
            colorAttachments: [{
                view: context.getCurrentTexture().createView(),
                clearValue: { r: 0, g: 0, b: 0, a: 0 },
                loadOp: 'clear',
                storeOp: 'store'
            }]
        });
        draw.setPipeline(render);
        draw.setBindGroup(0, renderGroup);
        draw.draw(6, self.count);
        draw.end();
        device.queue.submit([encoder.finish()]);
    });
}

export function stop(handle) {
    const self = instance;
    if (!self || (handle && handle !== self.handle)) return;
    instance = null;
    self.stopped = true;
    self.task?.();
    self.cleanup?.();
    try { self.device?.destroy(); } catch { /* already lost */ }
    self.canvas.remove();
}

gfx.onTierChanged((tier) => {
    if (tier !== 'full') stop(instance?.handle);
});
