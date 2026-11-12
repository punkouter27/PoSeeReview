// Haptic feedback: three fixed patterns over navigator.vibrate.
//
// Follows the audio preference — someone who muted the app did not ask to be buzzed instead —
// and can be switched off on its own, never on on its own.
//
// Every call is a no-op on failure: iOS exposes no Vibration API, some engines throw without
// transient user activation, and the OS can disable it. None of those is worth reporting.

const STORAGE_KEY = 'poseereview:haptics-enabled';

const PATTERNS = {
    tap: [10],
    confirm: [12, 60, 18],
    error: [40, 50, 40]
};

const state = {
    supported: false,
    // null = "follow the audio preference". true/false = the user chose explicitly.
    preference: null,
    audioEnabled: false,
    lastTapAt: -Infinity
};

function readStoredPreference() {
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        return raw === 'true' ? true : raw === 'false' ? false : null;
    } catch {
        return null; // Private mode. Follow audio for the session.
    }
}

function active() {
    // An explicit true is not an override: it only matters as "not false". Audio off is off.
    return state.supported && state.preference !== false && state.audioEnabled;
}

function fire(pattern) {
    if (!active()) return false;
    // Before the first gesture Chrome blocks the call and logs an error.
    if (navigator.userActivation && !navigator.userActivation.hasBeenActive) return false;
    try {
        return navigator.vibrate(pattern) !== false;
    } catch {
        return false;
    }
}

export const haptics = {
    init(audioEnabled = false) {
        try {
            state.supported = typeof navigator.vibrate === 'function';
        } catch {
            state.supported = false;
        }
        state.preference = readStoredPreference();
        state.audioEnabled = !!audioEnabled;
        return this.describe();
    },

    describe: () => ({
        supported: state.supported,
        enabled: active(),
        explicit: state.preference !== null
    }),

    /** Called by fx.js whenever the audio preference changes, since haptics inherit from it. */
    syncAudio(enabled) {
        state.audioEnabled = !!enabled;
        return active();
    },

    /** null clears an explicit choice and returns to following audio. */
    setEnabled(enabled) {
        state.preference = enabled === null ? null : !!enabled;
        try {
            if (state.preference === null) {
                localStorage.removeItem(STORAGE_KEY);
            } else {
                localStorage.setItem(STORAGE_KEY, String(state.preference));
            }
        } catch {
            // Session-only.
        }
        return active();
    },

    cancel() {
        if (navigator.userActivation && !navigator.userActivation.hasBeenActive) return;
        try { navigator.vibrate?.(0); } catch { /* nothing to cancel */ }
    },

    tap() {
        // Rapid repeats thin out rather than stacking.
        const now = performance.now();
        if (now - state.lastTapAt < 40) return false;
        state.lastTapAt = now;
        return fire(PATTERNS.tap);
    },

    confirm: () => fire(PATTERNS.confirm),
    error: () => fire(PATTERNS.error)
};
