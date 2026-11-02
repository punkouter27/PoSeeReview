// The audio comic's player glue: keeps the caption line and the speaking panel in step with the
// <audio> element. Imported by AudioComic.razor on its own (like map-view.js), never by fx.js.
// Nothing here throws into .NET.

const players = new Map();
let nextId = 1;

// A tenth of a second of silence: played inside the tap so the element is allowed to play
// later, once the recording (which can outlast the tap's activation window) has arrived.
const SILENCE = 'data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEAQB8AAIA+AAACABAAZGF0YQAAAAA=';

export function prime(audio) {
    try {
        if (!audio || audio.src) return;
        audio.src = SILENCE;
        audio.play()?.then(() => audio.pause()).catch(() => {});
    } catch { /* best effort */ }
}

/**
 * @param {HTMLAudioElement} audio
 * @param {HTMLElement} caption element whose text follows the current line
 * @param {{speaker: string, panel: number, text: string, start: number, end: number}[]} lines
 * @param {string} src the WAV
 * @returns {number} handle, 0 if it did not start
 */
export function play(audio, caption, lines, src) {
    try {
        if (!audio || !caption || !Array.isArray(lines)) return 0;
        const strip = document.querySelector('.comic-strip-container');
        let current = -1;
        const sync = () => {
            const t = audio.currentTime;
            const index = lines.findIndex((l) => t >= l.start && t < l.end + 0.3);
            if (index === current) return;
            current = index;
            const line = lines[index];
            caption.textContent = line ? `${line.speaker}: ${line.text}` : '';
            if (strip) {
                if (line) strip.dataset.speakingPanel = String(line.panel);
                else delete strip.dataset.speakingPanel;
            }
        };
        const end = () => {
            caption.textContent = '';
            if (strip) delete strip.dataset.speakingPanel;
        };
        audio.addEventListener('timeupdate', sync);
        audio.addEventListener('seeked', sync);
        audio.addEventListener('ended', end);
        audio.src = src;
        audio.play()?.catch(() => { /* blocked: the native controls are right there */ });
        const id = nextId++;
        players.set(id, { audio, sync, end, strip });
        return id;
    } catch {
        return 0;
    }
}

export function stop(id) {
    const player = players.get(id);
    if (!player) return;
    players.delete(id);
    try {
        player.audio.pause();
        player.audio.removeEventListener('timeupdate', player.sync);
        player.audio.removeEventListener('seeked', player.sync);
        player.audio.removeEventListener('ended', player.end);
        player.end();
    } catch { /* detached */ }
}
