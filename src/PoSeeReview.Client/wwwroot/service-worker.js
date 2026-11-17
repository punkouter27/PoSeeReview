// ============================================================================================
// PoSeeReview service worker
//
// NETWORK-FIRST, DELIBERATELY. Read this before switching anything to cache-first.
//
// Blazor WebAssembly verifies every framework file against the integrity hashes in
// blazor.boot.json. Those files are NOT fingerprinted by filename, so a cache-first worker that
// serves yesterday's `_framework/*.wasm` alongside today's boot manifest produces an integrity
// failure and a white screen — with no way for the user to recover except clearing site data.
// Network-first means the network decides what is current and the cache only ever answers when
// the network could not, which is the one behaviour that cannot break a deploy.
//
// Everything here is scoped to same-origin GETs. Comic images live on Blob Storage (a different
// origin, no CORS headers), so those responses would be opaque — cacheable only as blobs of
// unknown status, taking real quota for something that cannot be inspected. They pass straight
// through.
//
// ONE EXCEPTION, and it is narrow on purpose: `GET /api/comics/{placeId}/image`, the same-origin
// re-serve of a comic's image, is kept stale-while-revalidate so /my-comics has pictures offline.
// See comicImage() for why that is safe against a takedown. Nothing else under /api is cached.
// ============================================================================================

// Bump on any change to cached assets (wwwroot JS/CSS/icons). A new VERSION orphans the old
// posee-* caches on activate, which is the ONLY reliable way an already-controlled client
// stops running yesterday's audio.js/fx.js — network-first still answers from the runtime
// cache whenever a request fails, and a re-deployed file the worker never re-requested stays
// stale in that cache until this string changes.
const VERSION = 'v4';
const SHELL_CACHE = `posee-shell-${VERSION}`;
const RUNTIME_CACHE = `posee-runtime-${VERSION}`;

// Not versioned: these are the user's offline comics, and a JS deploy is no reason to drop them.
const IMAGE_CACHE = 'posee-comic-images';
const IMAGE_CACHE_MAX = 30;
const COMIC_IMAGE = /^\/api\/comics\/[^/]+\/image$/;

// The smallest set that makes an offline launch show this app rather than the browser's error
// page. Everything else is cached opportunistically as it is fetched.
const SHELL_ASSETS = [
    '/',
    '/offline.html',
    '/css/app.css',
    '/icons/icon-192.png',
    '/manifest.webmanifest'
];

self.addEventListener('install', event => {
    event.waitUntil((async () => {
        const cache = await caches.open(SHELL_CACHE);
        // addAll is all-or-nothing; one 404 would abort the whole install and leave the app with
        // no worker at all. Each asset is added independently so a missing one is survivable.
        await Promise.all(SHELL_ASSETS.map(async asset => {
            try {
                await cache.add(new Request(asset, { cache: 'reload' }));
            } catch {
                // Not fatal — this asset simply will not be available offline.
            }
        }));
        await self.skipWaiting();
    })());
});

self.addEventListener('activate', event => {
    event.waitUntil((async () => {
        const keys = await caches.keys();
        await Promise.all(keys
            .filter(key => key.startsWith('posee-')
                && key !== SHELL_CACHE && key !== RUNTIME_CACHE && key !== IMAGE_CACHE)
            .map(key => caches.delete(key)));
        await self.clients.claim();
    })());
});

/**
 * Paths that must never be served from a cache.
 *
 * `/api` and `/auth` are obvious. The diagnostics snapshot (`/api/diag`) is covered by `/api/`;
 * a cached one would be actively misleading — the `/diag` page is opened precisely when someone
 * needs to know the current state of the system.
 *
 * `/s/` and `/share/` are here for a different reason: both resolve content that moderation can
 * withdraw. A cached short-link redirect or a cached preview card would keep serving a comic
 * after it had been taken down, which is exactly the outcome the takedown path exists to
 * prevent. The card's own `Cache-Control` is already short for the same reason.
 */
function isNeverCached(url) {
    return url.pathname.startsWith('/api/')
        || url.pathname.startsWith('/auth/')
        || url.pathname.startsWith('/health')
        || url.pathname.startsWith('/s/')
        || url.pathname.startsWith('/share/');
}

/** Static assets worth keeping a copy of for offline use. */
function isCacheableAsset(url) {
    return /\.(?:css|js|wasm|dat|json|png|jpg|jpeg|svg|webp|woff2?|ttf)$/i.test(url.pathname)
        || url.pathname.startsWith('/_framework/')
        || url.pathname.startsWith('/_content/');
}

self.addEventListener('fetch', event => {
    const request = event.request;

    if (request.method !== 'GET') {
        return;
    }

    const url = new URL(request.url);

    // Cross-origin (comic blobs, fonts from a CDN) is left entirely alone.
    if (url.origin !== self.location.origin) {
        return;
    }

    // Checked before the /api rule below, which it is the single exception to.
    if (COMIC_IMAGE.test(url.pathname)) {
        event.respondWith(comicImage(event));
        return;
    }

    if (isNeverCached(url)) {
        return;
    }

    // A navigation is the one request where an offline answer is much better than an error: the
    // SPA shell can boot and show the app's own "you are offline" state.
    if (request.mode === 'navigate') {
        event.respondWith(handleNavigation(request));
        return;
    }

    if (isCacheableAsset(url)) {
        event.respondWith(networkFirst(request, RUNTIME_CACHE));
    }
});

async function handleNavigation(request) {
    try {
        const response = await fetch(request);

        // Only a real 200 is worth keeping. Caching a redirect or an error page as the shell is
        // how an app gets stuck showing its own 500.
        if (response.ok && response.type === 'basic') {
            const cache = await caches.open(SHELL_CACHE);
            cache.put('/', response.clone());
        }

        return response;
    } catch {
        return (await caches.match('/'))
            ?? (await caches.match('/offline.html'))
            ?? Response.error();
    }
}

/**
 * Stale-while-revalidate for one comic image. A cached copy answers at once; the network is
 * asked every time regardless, and what it says wins for next time.
 *
 * The revalidation is what makes caching this tolerable at all. A 404 (expired) or 451
 * (withheld) deletes the entry, so a takedown reaches the device the next time it is online —
 * the stale copy is shown at most once more. Any other failure, including being offline, leaves
 * the copy alone: that is the case the cache exists for.
 */
async function comicImage(event) {
    const { request } = event;
    const cache = await caches.open(IMAGE_CACHE);
    const cached = await cache.match(request);

    const refresh = (async () => {
        const response = await fetch(request);
        try {
            // Only an actual image: a 200 that is really a sign-in page must not be kept as one.
            if (response.ok && response.headers.get('content-type')?.startsWith('image/')) {
                // put() replaces the entry and re-appends it, so keys() stays oldest-first.
                await cache.put(request, response.clone());
                const keys = await cache.keys();
                await Promise.all(keys.slice(0, Math.max(0, keys.length - IMAGE_CACHE_MAX))
                    .map(key => cache.delete(key)));
            } else if (response.status === 404 || response.status === 451) {
                await cache.delete(request);
            }
        } catch {
            // Quota, or a response the cache refuses. The image itself still goes to the page.
        }
        return response;
    })();

    if (!cached) {
        return refresh;
    }

    event.waitUntil(refresh.catch(() => { }));
    return cached;
}

async function networkFirst(request, cacheName) {
    try {
        const response = await fetch(request);

        if (response.ok) {
            const cache = await caches.open(cacheName);
            cache.put(request, response.clone());
        }

        return response;
    } catch (error) {
        const cached = await caches.match(request);
        if (cached) {
            return cached;
        }
        throw error;
    }
}

// Lets the page trigger an immediate update instead of waiting for every tab to close.
self.addEventListener('message', event => {
    if (event.data === 'skipWaiting') {
        self.skipWaiting();
    }
});
