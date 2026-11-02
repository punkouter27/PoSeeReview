// Reading the comic plays it.
//
// The strip is a single <img> of stacked panels. Scrolling past it fires one note of that
// comic's own motif per panel as the panel crosses the middle of the screen — so the figure the
// score reveal played as a burst is re-heard, spread out, at the pace the reader chose.
//
// WHY THE VISUAL HALF IS NOT IN THIS FILE.
//
// The travelling highlight that goes with this is `animation-timeline: view()` in app.css, and it
// is there rather than here because the compositor can run it off the main thread. This module
// does only the part CSS cannot do: notice that a boundary was crossed and make a sound.
//
// WHY AN IntersectionObserver AND NOT A SCROLL HANDLER.
//
// A scroll handler fires continuously and would have to compute geometry on every event, on the
// same thread as the rest of the effects layer. An observer fires only on a crossing — two or
// four times for the whole page — and costs nothing in between. This is the case the API exists
// for; the reason app.css prefers view() for the FADE is that a fade is continuous, and this is
// not.
//
// The sentinels are zero-height absolutely-positioned divs, one per panel, laid down the strip.
// Observing the image itself would only say "the strip is visible", which is one event for the
// whole thing and cannot tell panel one from panel two.

const instances = new Map();
let nextId = 1;

/**
 * @param {HTMLElement} container the .comic-strip-container
 * @param {{ panels?: number, onPanel?: (index: number, total: number) => void }} options
 * @returns {number} handle, or 0 if it did not start
 */
export function start(container, options = {}) {
    if (!container || typeof IntersectionObserver !== 'function') {
        return 0;
    }

    // Reduced motion is not the gate here — this is sound, not movement, and someone who asked
    // for less animation did not ask for less audio. The audio preference is checked by the cue
    // itself, which is the only place that decision belongs.
    const panels = Math.max(1, Math.min(4, options.panels ?? 2));
    const onPanel = typeof options.onPanel === 'function' ? options.onPanel : null;
    if (!onPanel) {
        return 0;
    }

    let markers;
    try {
        markers = buildMarkers(container, panels);
    } catch {
        return 0;
    }
    if (!markers) return 0;

    // Each panel fires ONCE per mount. Without this, scrolling up and down over a strip plays the
    // motif repeatedly and turns a detail into an alarm.
    const fired = new Set();

    // The first callback reports every marker's starting state. One already above the middle was
    // passed before the reader arrived: marked fired silently, so mounting plays nothing.
    let primed = false;

    const observer = new IntersectionObserver((entries) => {
        if (!primed) {
            primed = true;
            for (const entry of entries) {
                if (entry.isIntersecting) fired.add(Number(entry.target.dataset.panelIndex));
            }
            return;
        }
        for (const entry of entries) {
            if (!entry.isIntersecting) continue;
            const index = Number(entry.target.dataset.panelIndex);
            if (!Number.isInteger(index) || fired.has(index)) continue;
            fired.add(index);
            try {
                onPanel(index, panels);
            } catch {
                // A cue must never be able to break scrolling.
            }
        }
    }, {
        // Everything above the middle of the viewport (the root reaches far above the top edge),
        // so "intersecting" means "this panel's centre has passed the middle of the screen". It
        // used to be a zero-height band AT the middle, which a 1px marker only touched when a
        // scroll step landed exactly on it — any normal step jumped past, and the note never played.
        rootMargin: '100000px 0px -50% 0px',
        threshold: 0
    });

    for (const marker of markers) {
        observer.observe(marker);
    }

    const id = nextId++;
    instances.set(id, { observer, markers, container });
    container.dataset.panelScrub = String(panels);
    return id;
}

function buildMarkers(container, panels) {
    const created = [];
    for (let i = 0; i < panels; i++) {
        const marker = document.createElement('div');
        marker.className = 'panel-marker';
        marker.dataset.panelIndex = String(i);
        marker.setAttribute('aria-hidden', 'true');
        // Centre of the i-th equal band down the strip. Percentages rather than pixels so a
        // rotation or a responsive reflow does not need this recomputed.
        marker.style.top = `${((i + 0.5) / panels) * 100}%`;
        container.appendChild(marker);
        created.push(marker);
    }
    return created.length ? created : null;
}

export function stop(id) {
    const instance = instances.get(id);
    if (!instance) return;

    instance.observer.disconnect();
    for (const marker of instance.markers) {
        marker.remove();
    }
    try {
        delete instance.container.dataset.panelScrub;
    } catch { /* detached */ }
    instances.delete(id);
}
