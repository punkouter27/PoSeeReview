// UI/UX and pipeline regression check. Run against a live app:
//     $env:BASE_URL = "https://localhost:5001"; node SCRIPTS/ui-check.mjs
//
// Covers the cascade-layer restructure (scoped CSS still applies, shared primitives win,
// Bootstrap gone), the design tokens, mobile overflow, and the two pipeline bugs this was
// written to catch: the Gemini image API surface and nearby-search ranking.
//
// NOTE: check 8 performs ONE real comic generation, which spends a paid image call.

import { chromium } from 'playwright';

const BASE = process.env.BASE_URL ?? 'https://localhost:5001';
const results = [];
const record = (n, p, d) => { results.push({ n, p }); console.log(`${p ? 'PASS' : 'FAIL'}  ${n}${d ? ` — ${d}` : ''}`); };

const browser = await chromium.launch({ args: ['--ignore-certificate-errors', '--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1366, height: 768 } });
const page = await ctx.newPage();
const failed = [];
page.on('response', r => { if (r.status() >= 400) failed.push(`${r.status()} ${r.url()}`); });

await page.goto(`${BASE}/login`, { waitUntil: 'domcontentloaded' });
await page.locator('.login-container').waitFor({ timeout: 30000 });
await page.getByRole('button', { name: /continue as guest/i }).click();
await page.locator('.index-container').waitFor({ timeout: 30000 });

// 1. No failed asset requests (a broken @import would show up here)
record('no failed asset requests', failed.length === 0, failed.slice(0, 4).join(' | ') || 'none');

// 2. Scoped CSS still applies through the layered @import
const scopedApplied = await page.evaluate(() => {
    const el = document.querySelector('.app-header h1');
    if (!el) return null;
    const cs = getComputedStyle(el);
    return { family: cs.fontFamily, size: cs.fontSize };
});
record('scoped CSS applies via @layer import',
    !!scopedApplied && /Bangers/i.test(scopedApplied.family),
    scopedApplied ? `${scopedApplied.family} @ ${scopedApplied.size}` : 'header missing');

// 3. New tokens are live
const tokens = await page.evaluate(() => {
    const cs = getComputedStyle(document.documentElement);
    const g = n => cs.getPropertyValue(n).trim();
    return {
        accentInk: g('--color-accent-ink'), muted: g('--color-text-muted'),
        borderStrong: g('--color-border-strong'), spaceMd: g('--space-md'), tap: g('--tap-target')
    };
});
record('new design tokens live',
    // These two hex values were stale: the accent ink and the muted text were both retuned for
    // WCAG contrast (and ColorContrastTests is what proves it), which left this check failing.
    tokens.accentInk === '#9B6407' && tokens.muted === '#656F71' && tokens.spaceMd === '1rem',
    JSON.stringify(tokens));

// 4. Bootstrap is gone
const bootstrapGone = await page.evaluate(() =>
    ![...document.styleSheets].some(s => (s.href ?? '').includes('bootstrap')));
record('bootstrap no longer loaded', bootstrapGone);

// 5. Shared .btn wins over any page redefinition (the cascade-layer fix)
const btnPill = await page.evaluate(() => {
    const b = document.createElement('button');
    b.className = 'btn btn-primary'; b.textContent = 'x';
    document.body.appendChild(b);
    const r = getComputedStyle(b).borderRadius;
    const h = getComputedStyle(b).minHeight;
    b.remove();
    return { r, h };
});
record('shared .btn primitive wins (pill + 44px target)',
    btnPill.r.startsWith('999') && parseFloat(btnPill.h) >= 44, JSON.stringify(btnPill));

// 6. Focus ring exists
const ring = await page.evaluate(() => {
    const b = document.querySelector('button, a[href]');
    if (!b) return null;
    b.focus();
    const cs = getComputedStyle(b);
    return { w: cs.outlineWidth, s: cs.outlineStyle };
});
record('focus-visible ring defined', !!ring, ring ? JSON.stringify(ring) : 'no focusable element');

// 7. No horizontal overflow at mobile width — on EVERY route, not just the landing page.
// This used to measure whichever page happened to be loaded, which is how /diagnostics shipped
// a 628px-wide document inside a 390px viewport: its config table could not shrink, and nothing
// here ever looked at that route. 320px is included because auto-fit grid floors only overflow
// once the container drops below them.
// Resize the signed-in page rather than opening a second one: a fresh tab re-boots the WASM
// runtime and re-runs the auth handshake, which is slow and unrelated to what is being measured.
const OVERFLOW_ROUTES = [
    ['/', '.index-container'],
    ['/leaderboard', '.leaderboard-container'],
    ['/diagnostics', '.diagnostics-container'],
    ['/my-comics', '.my-comics-container'],
];

async function checkOverflow(route, ready, width) {
    await page.setViewportSize({ width, height: 844 });
    await page.goto(`${BASE}${route}`, { waitUntil: 'domcontentloaded' });
    await page.locator(ready).waitFor({ timeout: 30000 }).catch(() => {});
    await page.waitForTimeout(900);
    const overflow = await page.evaluate(() => {
        const de = document.documentElement;
        const worst = [...document.querySelectorAll('body *')]
            .map(el => ({ el, w: el.getBoundingClientRect().width }))
            .filter(x => x.w > de.clientWidth + 1)
            .sort((a, b) => b.w - a.w)[0];
        return {
            scroll: de.scrollWidth, client: de.clientWidth,
            worst: worst ? `${worst.el.tagName.toLowerCase()}.${String(worst.el.className || '').split(' ')[0]} ${Math.round(worst.w)}px` : null,
        };
    });
    record(`no horizontal overflow on ${route} at ${width}px`,
        overflow.scroll <= overflow.client + 1, JSON.stringify(overflow));
}

for (const width of [320, 390]) {
    for (const [route, ready] of OVERFLOW_ROUTES) {
        await checkOverflow(route, ready, width);
    }
}

// 7b and 7c are landing-page, desktop assertions. The overflow walk above leaves the page on
// /my-comics at 390px, where .prompt-card does not exist and the My Comics link has legitimately
// collapsed to its 30px phone form — both checks failed on that page, not on the one they name.
await page.setViewportSize({ width: 1366, height: 768 });
await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded' });
await page.locator('.index-container').waitFor({ timeout: 30000 });

// 7b. Scoped CSS must reach CHILD-COMPONENT roots.
// This bug class is invisible in a stylesheet diff: a parent sheet writing `.prompt-card {
// max-width: ... }` when that class is handed to <RadzenCard> compiles to `.prompt-card[b-*]`,
// matches nothing, and silently does nothing at all. Six rules had shipped that way — including
// the comic card's own max-width and the entire Insights chart height — plus the two header
// NavLinks, which lost their 44px target and their `.active` state. Asserting a computed value
// that ONLY the scoped rule can supply is the one way to see it from outside the browser.
const scopedRoots = await page.evaluate(() => {
    const read = (sel, prop) => {
        const el = document.querySelector(sel);
        return el ? getComputedStyle(el)[prop] : null;
    };
    return {
        promptMax: read('.prompt-card', 'maxWidth'),        // RadzenCard root, via ::deep
        promptWidth: read('.prompt-card', 'width'),         // RadzenCard root, via ::deep
        myComicsMin: read('.nav-mycomics', 'minHeight'),    // NavLink root, via ::deep
        myComicsDisplay: read('.nav-mycomics', 'display'),  // NavLink root, via ::deep
    };
});
record('scoped CSS reaches child-component roots',
    scopedRoots.promptMax === '520px' && parseFloat(scopedRoots.myComicsMin) >= 44,
    JSON.stringify(scopedRoots));

// 7c. Tap targets. 24px is the WCAG 2.5.8 floor; below it a control is a mis-tap on a phone.
// It is not asserted at 44px because a link sitting inline in a sentence is explicitly exempt
// and there are legitimate ones (the disclosure's "report a problem").
const smallTargets = await page.evaluate(() => {
    const bad = [];
    for (const el of document.querySelectorAll('a[href], button, [role="button"], input, select')) {
        const r = el.getBoundingClientRect();
        if (r.width === 0 || r.height === 0) continue;
        // Parked off-screen until focused (the skip link at left: -9999px). Its resting 1x1 box
        // is not a tap target; nobody can reach it by touch.
        if (r.right < 0 || r.bottom < 0) continue;
        // An expanded ::after hit box does not change getBoundingClientRect, and the design
        // system uses that trick deliberately — so only flag a control that is also not covered
        // by one of the shared primitives that supplies the bigger target.
        const hasHitBox = getComputedStyle(el, '::after').position === 'absolute';
        if (Math.min(r.width, r.height) < 24 && !hasHitBox) {
            bad.push(`${(el.getAttribute('aria-label') || el.textContent || el.tagName).trim().slice(0, 24)} ${Math.round(r.width)}x${Math.round(r.height)}`);
        }
    }
    return bad;
});
record('no sub-24px tap targets on the landing page', smallTargets.length === 0,
    smallTargets.slice(0, 5).join(' | ') || 'none');

await page.setViewportSize({ width: 1366, height: 768 });
await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded' });
await page.locator('.index-container').waitFor({ timeout: 30000 });

// 8. End-to-end comic generation through the streaming endpoint (the reported failure).
//    One real generation — this spends a paid image call, deliberately just the one.
const gen = await page.evaluate(async () => {
    const search = await fetch('/api/restaurants/search?location=20020&limit=5', {
        headers: { 'X-Correlation-ID': 'ui-verify' }
    });
    if (!search.ok) return { stage: 'search', status: search.status };
    const data = await search.json();
    const first = data.restaurants?.[0];
    if (!first) return { stage: 'search', status: 'no results' };

    const res = await fetch(`/api/comics/${first.placeId}/stream`, { method: 'POST' });
    if (!res.ok) return { stage: 'stream-open', status: res.status };

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buf = '', phases = [], done = null, error = null;
    while (true) {
        const { value, done: finished } = await reader.read();
        if (finished) break;
        buf += decoder.decode(value, { stream: true });
        const lines = buf.split(String.fromCharCode(10));
        buf = lines.pop();
        for (const line of lines) {
            if (!line.startsWith('data:')) continue;
            const evt = JSON.parse(line.slice(5).trim());
            if (evt.kind === 'phase') phases.push(evt.phase);
            if (evt.kind === 'complete') done = evt.comic;
            if (evt.kind === 'error') error = evt;
        }
    }
    return {
        stage: 'done', name: first.name, rating: first.averageRating,
        placeId: first.placeId,
        phases, error,
        comic: done ? { score: done.strangenessScore, url: !!done.blobUrl } : null
    };
});
console.log('    generation:', JSON.stringify(gen));
record('comic generation succeeds end-to-end', gen.stage === 'done' && !!gen.comic && !gen.error,
    gen.comic ? `"${gen.name}" score ${gen.comic.score}, image ${gen.comic.url}, phases [${gen.phases}]`
              : `stage=${gen.stage} ${JSON.stringify(gen.error ?? gen.status)}`);

// 8b. The comic page fits ONE desktop screen.
// It measured 1854px tall inside a 946px viewport while the card used only 64% of the width —
// scrolling vertically and wasting horizontal room at the same time. It is a two-column layout
// past 64rem now, with the strip capped to the space left below the shell header.
if (gen.placeId) {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`${BASE}/comic/${gen.placeId}`, { waitUntil: 'domcontentloaded' });
    await page.locator('.comic-strip-image').waitFor({ timeout: 30000 }).catch(() => {});
    await page.waitForTimeout(1500);
    const comicFit = await page.evaluate(() => {
        const de = document.documentElement;
        const img = document.querySelector('.comic-strip-image');
        const card = document.querySelector('.comic-container');
        return {
            screens: +(de.scrollHeight / window.innerHeight).toFixed(2),
            imgBottom: img ? Math.round(img.getBoundingClientRect().bottom) : null,
            vh: window.innerHeight,
            cardWidth: card ? Math.round(card.getBoundingClientRect().width) : null,
        };
    });
    record('comic page fits one desktop screen',
        comicFit.screens <= 1.15 && comicFit.imgBottom !== null && comicFit.imgBottom <= comicFit.vh,
        JSON.stringify(comicFit));

    // 8c. Portrait overflow on the comic route itself. Check 7 walked every route EXCEPT this
    // one — the page carrying the strip, the score ring, four overlay canvases, the action bar
    // and the reaction bar — because it needs a comic to exist first. Now one does.
    for (const width of [320, 390]) {
        await checkOverflow(`/comic/${gen.placeId}`, '.comic-strip-image', width);
    }
    await page.setViewportSize({ width: 1366, height: 768 });
}

// 9. Nearby search now ranks by distance, not popularity.
const ranking = await page.evaluate(async () => {
    const r = await fetch('/api/restaurants/search?location=20020&limit=20');
    if (!r.ok) return null;
    const d = await r.json();
    const rs = d.restaurants ?? [];
    return {
        count: rs.length,
        maxDistanceKm: Math.max(...rs.map(x => x.distance ?? 0)),
        ratings: rs.map(x => x.averageRating),
        below4: rs.filter(x => (x.averageRating ?? 0) < 4).length,
        lowReviewCount: rs.filter(x => (x.totalReviews ?? 0) < 500).length
    };
});
console.log('    ranking:', JSON.stringify(ranking));
record('nearby results are distance-ranked, not popularity-ranked',
    !!ranking && ranking.maxDistanceKm < 3,
    ranking ? `furthest ${ranking.maxDistanceKm.toFixed(2)}km, ${ranking.below4}/${ranking.count} under 4 stars, ${ranking.lowReviewCount} with <500 reviews` : 'search failed');

await browser.close();
const bad = results.filter(r => !r.p);
console.log(`\n${results.length - bad.length}/${results.length} checks passed`);
process.exit(bad.length ? 1 : 0);
