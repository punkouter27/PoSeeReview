// The comic as a 9:16 clip for Reels / Shorts / TikTok, made in the browser: the strip is drawn
// onto a canvas with the camera on the speaking panel, the audio comic (when there is one) is the
// soundtrack, and MediaRecorder films it in real time. Imported by ComicVideo.razor on its own
// (like audio-comic.js), never by fx.js. Nothing here throws into .NET.

const W = 1080;
const H = 1920;
const FPS = 30;
const SILENT_SECONDS_PER_PANEL = 4;
const OUTRO = 1.5;
const MAX_SECONDS = 90;

// MP4 first: Instagram and TikTok refuse WebM. Chrome 126+ and Safari record MP4; Firefox WebM.
const TYPES = [
    'video/mp4;codecs=avc1.42E01E,mp4a.40.2',
    'video/mp4',
    'video/webm;codecs=vp9,opus',
    'video/webm',
];

const clips = new Map();
let nextId = 1;

const mimeType = () => TYPES.find((t) => MediaRecorder.isTypeSupported(t));

export function supported() {
    try {
        return typeof MediaRecorder === 'function'
            && typeof HTMLCanvasElement.prototype.captureStream === 'function'
            && !!mimeType();
    } catch {
        return false;
    }
}

const token = (style, name, fallback) => style.getPropertyValue(name).trim() || fallback;
const ease = (x) => x * x * (3 - 2 * x);

function wrap(ctx, text, maxWidth) {
    const lines = [];
    let line = '';
    for (const word of text.split(/\s+/).filter(Boolean)) {
        const next = line ? `${line} ${word}` : word;
        if (line && ctx.measureText(next).width > maxWidth) {
            lines.push(line);
            line = word;
        } else {
            line = next;
        }
    }
    if (line) lines.push(line);
    return lines;
}

async function loadAudio(audioCtx, audioUrl) {
    if (!audioUrl) return null;
    try {
        const response = await fetch(audioUrl, { credentials: 'same-origin' });
        if (!response.ok) return null;
        return await audioCtx.decodeAudioData(await response.arrayBuffer());
    } catch {
        return null;
    }
}

/**
 * Films the clip. Resolves when it is finished (about as long as the clip), not before.
 * @param {HTMLProgressElement} progress filled 0..1 while filming
 * @param {string} placeId
 * @param {string} title restaurant name
 * @param {number} score
 * @param {number} panels 1 or 2, stacked vertically (ComicImagePrompt's two layouts)
 * @param {string} url the short link, printed at the bottom
 * @param {{speaker: string, panel: number, text: string, start: number, end: number}[]|null} lines
 * @param {string|null} audioUrl same-origin WAV of the audio comic
 * @returns {Promise<number>} handle, 0 if it could not be made
 */
export async function make(progress, placeId, title, score, panels, url, lines, audioUrl) {
    let audioCtx = null;
    try {
        if (!supported()) return 0;
        // Created first, while the tap's activation is still fresh: Safari starts it suspended otherwise.
        audioCtx = new AudioContext();

        const imageResponse = await fetch(`/api/comics/${encodeURIComponent(placeId)}/image`, { credentials: 'same-origin' });
        if (!imageResponse.ok) return 0;
        const image = await createImageBitmap(await imageResponse.blob());
        const buffer = await loadAudio(audioCtx, audioUrl);
        await Promise.allSettled([document.fonts.load('96px Bangers'), document.fonts.load('bold 48px "DM Sans"')]);

        const spoken = buffer && Array.isArray(lines) ? lines : [];
        const panelCount = Math.max(1, Math.min(2, panels | 0));
        const duration = Math.min(MAX_SECONDS, buffer ? buffer.duration + OUTRO : panelCount * SILENT_SECONDS_PER_PANEL + OUTRO);

        const style = getComputedStyle(document.documentElement);
        const colors = {
            ground: token(style, '--surface-inverse', '#140A28'),
            ink: token(style, '--color-on-dark', '#FFFFFF'),
            muted: token(style, '--color-on-dark-muted', 'rgba(255,255,255,0.7)'),
            accent: token(style, '--color-accent', '#F59E0B'),
            brand: token(style, '--color-brand', '#7C3AED'),
        };

        const canvas = document.createElement('canvas');
        canvas.width = W;
        canvas.height = H;
        const ctx = canvas.getContext('2d');

        // The strip's box, and the image fitted into it.
        const box = { x: 40, y: 430, w: W - 80, h: 1060 };
        const fit = Math.min(box.w / image.width, box.h / image.height);
        const camera = { zoom: 1, cy: 0.5 };

        const titleFont = '96px Bangers, Impact, sans-serif';
        ctx.font = titleFont;
        const titleLines = wrap(ctx, title, W - 120).slice(0, 2);

        const draw = (t) => {
            ctx.fillStyle = colors.ground;
            ctx.fillRect(0, 0, W, H);

            ctx.textAlign = 'center';
            ctx.textBaseline = 'alphabetic';
            ctx.fillStyle = colors.muted;
            ctx.font = 'bold 40px "DM Sans", sans-serif';
            ctx.fillText('POSEEREVIEW', W / 2, 120);

            ctx.fillStyle = colors.ink;
            ctx.font = titleFont;
            titleLines.forEach((line, i) => ctx.fillText(line, W / 2, 230 + i * 100));

            ctx.fillStyle = colors.accent;
            // DM Sans, not Bangers: Bangers draws 7 and 1 alike, and 87 read as 81.
            ctx.font = 'bold 72px "DM Sans", sans-serif';
            ctx.fillText(`${score}/100 STRANGE`, W / 2, 230 + titleLines.length * 100 + 10);

            // Camera: on the speaking panel, or on each panel in turn when there are no voices;
            // the whole strip again for the outro.
            const line = spoken.find((l) => t >= l.start && t < l.end + 0.3);
            const outro = t > duration - OUTRO;
            const panel = line ? line.panel : Math.min(panelCount, Math.floor(t / SILENT_SECONDS_PER_PANEL) + 1);
            const target = outro
                ? { zoom: 1, cy: 0.5 }
                : panelCount === 1
                    ? { zoom: 1 + 0.1 * ease(Math.min(1, t / duration)), cy: 0.5 }
                    : { zoom: 1.35, cy: (Math.max(1, Math.min(2, panel)) - 0.5) / 2 };
            camera.zoom += (target.zoom - camera.zoom) * 0.08;
            camera.cy += (target.cy - camera.cy) * 0.08;

            const scale = fit * camera.zoom;
            const dw = image.width * scale;
            const dh = image.height * scale;
            const dy = Math.min(box.y, Math.max(box.y + box.h - dh, box.y + box.h / 2 - camera.cy * dh));
            ctx.save();
            ctx.beginPath();
            ctx.rect(box.x, box.y, box.w, box.h);
            ctx.clip();
            ctx.drawImage(image, box.x + (box.w - dw) / 2, dy, dw, dh);
            ctx.restore();
            ctx.strokeStyle = colors.ink;
            ctx.lineWidth = 6;
            ctx.strokeRect(box.x, box.y, box.w, box.h);

            if (line) {
                ctx.font = 'bold 50px "DM Sans", sans-serif';
                const textLines = wrap(ctx, `${line.speaker}: ${line.text}`, W - 160).slice(0, 4);
                const top = box.y + box.h + 50;
                ctx.fillStyle = colors.brand;
                ctx.fillRect(60, top, W - 120, textLines.length * 64 + 40);
                ctx.fillStyle = colors.ink;
                textLines.forEach((l, i) => ctx.fillText(l, W / 2, top + 72 + i * 64));
            }

            ctx.fillStyle = colors.muted;
            ctx.font = 'bold 36px "DM Sans", sans-serif';
            if (buffer) ctx.fillText('AI dramatization · based on the reviews', W / 2, H - 150);
            ctx.fillStyle = colors.ink;
            ctx.font = 'bold 48px "DM Sans", sans-serif';
            ctx.fillText(url.replace(/^https?:\/\//, ''), W / 2, H - 80);
        };

        const stream = canvas.captureStream(FPS);
        if (buffer) {
            const destination = audioCtx.createMediaStreamDestination();
            const source = audioCtx.createBufferSource();
            source.buffer = buffer;
            source.connect(destination);
            destination.stream.getAudioTracks().forEach((track) => stream.addTrack(track));
            await audioCtx.resume().catch(() => {});
            source.start();
        }

        const type = mimeType();
        const recorder = new MediaRecorder(stream, { mimeType: type, videoBitsPerSecond: 6_000_000 });
        const chunks = [];
        recorder.ondataavailable = (e) => { if (e.data.size) chunks.push(e.data); };

        const id = nextId++;
        const clip = { blob: null, type, cancelled: false, recorder };
        clips.set(id, clip);

        // A timer, not requestAnimationFrame: the shared rAF loop stops at the `off` tier, and the
        // film must keep rolling whatever the effects tier is.
        const started = buffer ? audioCtx.currentTime : performance.now() / 1000;
        const now = () => (buffer ? audioCtx.currentTime : performance.now() / 1000) - started;
        draw(0);
        recorder.start(250);

        await new Promise((resolve) => {
            const timer = setInterval(() => {
                const t = now();
                if (clip.cancelled || t >= duration) {
                    clearInterval(timer);
                    recorder.onstop = resolve;
                    recorder.stop();
                    return;
                }
                draw(t);
                if (progress) progress.value = t / duration;
            }, 1000 / FPS);
        });

        stream.getTracks().forEach((track) => track.stop());
        image.close();
        if (clip.cancelled) {
            clips.delete(id);
            return 0;
        }
        clip.blob = new Blob(chunks, { type: type.split(';')[0] });
        if (progress) progress.value = 1;
        return id;
    } catch (error) {
        console.error('comic video:', error);
        return 0;
    } finally {
        audioCtx?.close().catch(() => {});
    }
}

const fileFor = (clip, title) => {
    const slug = title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 40) || 'comic';
    return new File([clip.blob], `poseereview-${slug}.${clip.type.includes('mp4') ? 'mp4' : 'webm'}`, { type: clip.blob.type });
};

/** True when this browser can hand the clip to the share sheet (otherwise share() downloads it). */
export function canShare(id, title) {
    const clip = clips.get(id);
    try {
        return !!clip?.blob && !!navigator.canShare?.({ files: [fileFor(clip, title)] });
    } catch {
        return false;
    }
}

/**
 * Must run inside a tap: the share sheet needs a fresh activation, which the filming outlasts.
 * @returns {Promise<'shared'|'cancelled'|'downloaded'|'failed'>}
 */
export async function share(id, title, text) {
    const clip = clips.get(id);
    if (!clip?.blob) return 'failed';
    const file = fileFor(clip, title);
    try {
        if (navigator.canShare?.({ files: [file] })) {
            await navigator.share({ title, text, files: [file] });
            return 'shared';
        }
    } catch (error) {
        if (error?.name === 'AbortError') return 'cancelled';
    }
    try {
        const href = URL.createObjectURL(file);
        const a = Object.assign(document.createElement('a'), { href, download: file.name });
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(href), 10_000);
        return 'downloaded';
    } catch {
        return 'failed';
    }
}

/** Stops a clip that is still filming (the page is going away before make() returned its id). */
export function cancelFilming() {
    for (const clip of clips.values()) {
        if (!clip.blob) clip.cancelled = true;
    }
}

/** Drops a finished clip. */
export function release(id) {
    const clip = clips.get(id);
    if (!clip) return;
    clips.delete(id);
}
