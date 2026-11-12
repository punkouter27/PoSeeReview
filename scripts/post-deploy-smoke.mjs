// Post-deploy smoke test (NET_RULES 5.4).
//
// Runs against a deployed environment and verifies:
//   1. Blazor render tree initialises (the WASM runtime boots and replaces the loading shell)
//   2. /health responds and reports a non-failed status
//
// The former /diag secret-masking check was removed: /diag sits behind the unsupported-user-agent
// guard, so an anonymous scripted fetch always gets a 400 and the check could never pass here.
//
// Usage: BASE_URL=https://app-poseereview2.azurewebsites.net node SCRIPTS/post-deploy-smoke.mjs
//
// Anonymous only: every endpoint touched here is AllowAnonymous, so the script needs no
// credentials and must never be given any.

import { chromium } from 'playwright';

const BASE_URL = (process.env.BASE_URL || 'https://localhost:5001').replace(/\/+$/, '');
const TIMEOUT_MS = Number(process.env.SMOKE_TIMEOUT_MS || 60_000);
const IGNORE_TLS = process.env.SMOKE_IGNORE_TLS === '1';

const results = [];
const record = (check, pass, value) => {
  results.push({ check, pass, value });
  console.log(`  [${pass ? 'PASS' : 'FAIL'}] ${check}${value === undefined ? '' : ` (${value})`}`);
};

async function checkHealth() {
  const res = await fetch(`${BASE_URL}/health`, { signal: AbortSignal.timeout(TIMEOUT_MS) });
  record('/health reachable', res.ok, res.status);
  if (!res.ok) return;

  const body = await res.text();
  let status = body.trim();
  try {
    status = JSON.parse(body).status ?? status;
  } catch {
    // /health may return a bare string; the text form is already usable.
  }
  record('/health not Unhealthy', !/unhealthy/i.test(status), status.slice(0, 120));
}

async function checkBlazorRenderTree() {
  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ ignoreHTTPSErrors: IGNORE_TLS });
  const page = await context.newPage();

  const consoleErrors = [];
  page.on('console', (msg) => msg.type() === 'error' && consoleErrors.push(msg.text()));
  page.on('pageerror', (err) => consoleErrors.push(`pageerror: ${err.message}`));

  try {
    const response = await page.goto(`${BASE_URL}/`, { waitUntil: 'domcontentloaded', timeout: TIMEOUT_MS });
    record('SPA shell served', (response?.status() ?? 0) < 400, response?.status());

    // The static shell ships <div id="app"> containing only the loading svg. Once the WASM
    // runtime boots, Blazor replaces that subtree with the rendered component hierarchy —
    // so the disappearance of .loading-progress IS the render-tree initialisation signal.
    await page.waitForFunction(
      () => {
        const app = document.getElementById('app');
        return app !== null && app.querySelector('.loading-progress') === null && app.children.length > 0;
      },
      { timeout: TIMEOUT_MS },
    );
    record('Blazor render tree initialised', true);

    // A booted app renders the brand whatever the auth state: the nav's .brand-name when signed
    // in, or the login page's own (BareLayout has no nav) — which is where an anonymous smoke
    // run always lands, and why checking only .brand-name failed every deploy after a8f8f00.
    const brand = await page.locator('.brand-name, .login-brand-name').first().textContent({ timeout: TIMEOUT_MS });
    record('header branding rendered', brand?.trim() === 'PoSeeReview', brand?.trim());

    record(
      'no console errors during boot',
      consoleErrors.length === 0,
      consoleErrors.length === 0 ? 'none' : consoleErrors.slice(0, 3).join(' | '),
    );
  } catch (err) {
    record('Blazor render tree initialised', false, err.message);
  } finally {
    await browser.close();
  }
}

console.log(`=== post-deploy smoke: ${BASE_URL} ===`);
await checkHealth();
await checkBlazorRenderTree();

const failed = results.filter((r) => !r.pass);
console.log(failed.length === 0 ? '\nALL CHECKS PASSED' : `\n${failed.length} CHECK(S) FAILED`);
process.exit(failed.length === 0 ? 0 : 1);
