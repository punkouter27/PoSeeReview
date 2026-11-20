// Cross-fade between routes using the View Transitions API, plus a shared-element morph from a
// tapped restaurant card into the comic view.
//
// Blazor's router does not integrate with document.startViewTransition, and there is no hook that
// brackets "DOM is about to change" / "DOM has changed". So this drives it from navigation
// instead: intercept the click on an internal link, TAKE IT AWAY FROM BLAZOR, snapshot the
// current document, navigate from inside the transition's update callback, and end the
// transition once the new route has rendered.
//
// Taking the click is the load-bearing part. startViewTransition captures the old state on the
// NEXT frame, and Blazor's own link handler renders the new route synchronously in the same task
// as the click — so letting it through meant the "old" snapshot was already the new page, and
// every transition blended the destination with itself. preventDefault in this capture-phase
// listener makes Blazor's handler stand down (it ignores default-prevented clicks), and
// Blazor.navigateTo runs only once the old frame is safely captured.
//
// Deliberately conservative:
//   * Unsupported browsers get today's hard cut. No polyfill, no JS-driven fade — a hand-rolled
//     crossfade of a whole SPA is exactly the kind of thing that fights the compositor.
//   * prefers-reduced-motion disables it outright. A full-page cross-fade is precisely the
//     "large moving content" reduced-motion exists to suppress.
//   * A safety timeout always resolves the transition. If Blazor throws mid-navigation, an
//     unresolved startViewTransition leaves the page frozen under a snapshot — a blank app.

const SUPPORTED = typeof document !== 'undefined' && typeof document.startViewTransition === 'function';
const SETTLE_TIMEOUT_MS = 600;

let enabled = false;
let pending = null;

/** Whether this navigation is a card -> comic, so settle() knows to tag the arriving element. */
let morphArmed = false;

/** Fired on every transition the app opens, so fx.js can pan a cue in the direction of travel. */
let onNavigate = null;

/**
 * How "deep" each route is, for deciding whether a navigation is a step forward or a step back.
 *
 * A transition that always slides the same way is a cross-fade with extra steps: direction is
 * only information if going back reverses it. Depth rather than history length because this is a
 * browsing app, not a wizard — arriving at a comic from a link is forward even on a first load,
 * and returning to the list it came from is back regardless of how the user got there.
 */
const ROUTE_DEPTH = [
    [/^\/comic\//, 3],
    [/^\/moderation/, 2],
    [/^\/diag/, 2],
    [/^\/insights/, 2],
    [/^\/my-comics/, 2],
    [/^\/leaderboard/, 2]
];

function depthOf(pathname) {
    for (const [pattern, depth] of ROUTE_DEPTH) {
        if (pattern.test(pathname)) return depth;
    }
    return 1;   // "/" and anything unrecognised is the shallow end.
}

function reducedMotion() {
    try {
        return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    } catch {
        return false;
    }
}

/**
 * Every place in the app a comic can be opened FROM.
 *
 * All four are lists of the same thing — a comic you can tap — and every one of them used to
 * drop the user onto the comic page with no relationship to the row they came from. The morph is
 * what makes an app of many lists feel like one place: the thing you touched is the thing that
 * arrives.
 *
 *   [data-comic-card] — a restaurant card on discovery
 *   .comic-row          — the live Hall of Fame, the weekly archive and /my-comics
 */
const MORPH_SOURCES = '[data-comic-card], .comic-row';

/** The destination half of the pair. Tagged on arrival — see tagMorphTarget. */
const MORPH_TARGET = '.comic-strip-container';

const MORPH_NAME = 'comic-morph';

/**
 * Marks the tapped card so CSS can morph it into the comic panel. view-transition-name must be
 * unique per document, so exactly one element may carry it at a time — it is cleared on the way
 * out and re-applied per navigation.
 */
function tagMorphSource(element) {
    clearMorphTags();
    if (element) {
        element.style.viewTransitionName = MORPH_NAME;
    }
}

/**
 * Tags the arriving comic with the SAME name, which is what actually makes this a morph.
 *
 * This was the missing half. With the name on only the outgoing card, the browser has one
 * element and nothing to pair it with, so the "morph" degraded to the source fading out — the
 * card vanished and the comic cross-faded in from nowhere. A shared-element transition needs the
 * name present in both the before and after snapshots.
 *
 * Called from settle(), which runs after Blazor has rendered the destination and before the
 * transition callback takes the new snapshot. Doing it any earlier is impossible: the element
 * does not exist yet.
 */
function tagMorphTarget() {
    if (!morphArmed) return;
    try {
        const target = document.querySelector(MORPH_TARGET);
        if (target) {
            target.style.viewTransitionName = MORPH_NAME;
        }
    } catch {
        // Degrades to the plain cross-fade, which is a complete transition on its own.
    }
}

function clearMorphTags() {
    for (const el of document.querySelectorAll('[style*="view-transition-name"]')) {
        el.style.viewTransitionName = '';
    }
}

/** Paths served by the Blazor router rather than by an API endpoint or a static file. */
const NON_SPA_PREFIXES = ['/auth', '/api', '/health', '/_framework', '/_content'];

function isSpaRoute(pathname) {
    if (NON_SPA_PREFIXES.some((prefix) => pathname === prefix || pathname.startsWith(prefix + '/'))) {
        return false;
    }
    // A trailing extension means a static asset, not a route.
    return !/\.[a-z0-9]{2,5}$/i.test(pathname);
}

function onDocumentClick(event) {
    if (!enabled || event.defaultPrevented || event.button !== 0) return;
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;

    const anchor = event.target.closest('a[href]');
    if (!anchor) return;

    // Same-origin, same-tab navigations only. An external link or a download must not be
    // wrapped in a transition that never completes.
    const url = new URL(anchor.href, document.baseURI);
    if (url.origin !== location.origin) return;
    if (anchor.target && anchor.target !== '_self') return;
    if (anchor.hasAttribute('download')) return;
    if (url.pathname === location.pathname) return;

    // Only routes the Blazor router owns. A link to a server endpoint (/auth/login/microsoft,
    // /api/..., a static file) causes a FULL document navigation, and a transition opened for
    // one snapshots a document that is about to be destroyed — Blazor never renders, so nothing
    // ever calls settle(). The timeout would eventually release it, but for those hundreds of
    // milliseconds the page is frozen under a stale image for no benefit at all.
    if (!isSpaRoute(url.pathname)) return;

    // Card -> comic gets the shared-element morph; everything else is a plain cross-fade.
    const card = event.target.closest(MORPH_SOURCES);
    morphArmed = Boolean(card) && url.pathname.startsWith('/comic/');
    tagMorphSource(morphArmed ? card : null);

    // Direction is stamped on the document element BEFORE the snapshot, so the CSS below can pick
    // which way the incoming page slides in. Cleared when the transition finishes — leaving it
    // set would make the next transition inherit a direction it never chose.
    const direction = depthOf(url.pathname) >= depthOf(location.pathname) ? 'forward' : 'back';
    try {
        document.documentElement.dataset.navDirection = direction;
        // Where the finger landed: the ink blot that carries a back navigation is centred there
        // as it grows (app.css, `posee-ink-blot`).
        const style = document.documentElement.style;
        style.setProperty('--vt-x', `${Math.round(event.clientX)}px`);
        style.setProperty('--vt-y', `${Math.round(event.clientY)}px`);
    } catch { /* the transition simply cross-fades */ }

    if (onNavigate) {
        try {
            onNavigate(direction, event.clientX);
        } catch {
            // A cue must never be able to cancel a navigation.
        }
    }

    event.preventDefault();
    beginTransition(() => navigateTo(url));
}

function navigateTo(url) {
    const path = url.pathname + url.search + url.hash;
    if (typeof window.Blazor?.navigateTo === 'function') {
        window.Blazor.navigateTo(path);
    } else {
        location.assign(url.href);
    }
}

/**
 * Opens a view transition whose update callback performs the navigation, then waits for
 * Blazor's post-render hook (settle) to say the destination is in the DOM.
 *
 * The arrival gate is created INSIDE the callback, after navigating: a settle() from some
 * unrelated re-render of the outgoing page, landing between the click and the callback, would
 * otherwise release the gate early and the "after" snapshot would be the old page again.
 */
function beginTransition(navigate) {
    let navigated = false;
    const go = () => {
        if (navigated) return;
        navigated = true;
        navigate();
    };

    // One at a time. A second click mid-transition still navigates — it just does not animate.
    if (pending) {
        go();
        return;
    }

    let release = null;
    const transition = document.startViewTransition(async () => {
        const arrived = new Promise((resolve) => { release = resolve; });
        go();
        await Promise.race([arrived, new Promise((r) => setTimeout(r, SETTLE_TIMEOUT_MS))]);
        // One task so anything Blazor queued behind settle() lands before the "after" snapshot.
        // NOT requestAnimationFrame: rendering is suppressed while this callback is pending, so
        // rAF never fires inside it — waiting on one stalled every navigation until the
        // browser's own DOM-update timeout aborted the transition, and no animation ever ran.
        await new Promise((r) => setTimeout(r, 0));
    });

    pending = {
        settle() {
            release?.();
        }
    };

    transition.finished.finally(() => {
        // A skipped transition must still arrive somewhere: the click was taken from Blazor.
        go();
        clearMorphTags();
        morphArmed = false;
        try {
            delete document.documentElement.dataset.navDirection;
        } catch { /* nothing to undo */ }
        pending = null;
    }).catch(() => { /* skipTransition or an interrupted navigation */ });
}

/**
 * A full-page navigation destroys the document mid-transition. Releasing the gate here means the
 * outgoing page is never left frozen under a snapshot while the browser tears it down.
 */
function onPageHide() {
    pending?.settle();
}

/**
 * @param {{ onNavigate?: (direction: 'forward'|'back', clientX: number) => void }} options
 *        `onNavigate` is how fx.js pans a cue in the direction of travel. Passed in rather than
 *        imported, for the reason every coupling in this layer works that way: this module must
 *        not learn that the app makes noise, and if nobody supplies a callback nothing notices.
 */
export function init(options = {}) {
    onNavigate = typeof options.onNavigate === 'function' ? options.onNavigate : null;
    enabled = SUPPORTED && !reducedMotion();
    if (enabled) {
        document.addEventListener('click', onDocumentClick, true);
        window.addEventListener('pagehide', onPageHide);
        document.documentElement.dataset.viewTransitions = 'on';
    }
    return { supported: SUPPORTED, enabled };
}

/**
 * Called from Blazor after the destination route has rendered.
 *
 * The arriving comic is tagged HERE, immediately before the gate is released: the transition
 * callback takes the "after" snapshot once this resolves, and the element does not exist any
 * earlier than this. Tagging after releasing would race the snapshot.
 */
export function settle() {
    tagMorphTarget();
    pending?.settle();
}
