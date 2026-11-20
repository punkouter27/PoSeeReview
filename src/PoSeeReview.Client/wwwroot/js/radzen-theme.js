// Radzen's theme and script, loaded only by the operator pages (/moderation) that
// use Radzen components — never by the consumer app, which has its own design system.
//
// The theme goes into `@layer vendor` (vendor.js), so app.css's `--rz-*` bridge (brand colours,
// dark mode) wins. The `-base` theme is the one whose only element rule is `body { margin: 0 }`;
// the full theme restyles h1-h6 and body.

import { loadScript, loadLayeredStyle } from './vendor.js';

const BASE = '_content/Radzen.Blazor/';
const BOM = String.fromCharCode(0xfeff);

let loading;

export function load() {
    loading ??= Promise.all([
        // Its font URLs are relative to the sheet; inlined, they would resolve against the page.
        loadLayeredStyle('radzen', `${BASE}css/material-base.css`, {
            rewrite: (css) => (css.startsWith(BOM) ? css.slice(1) : css)
                .replaceAll('url("../fonts/', `url("${BASE}fonts/`)
        }),
        loadScript(`${BASE}Radzen.Blazor.min.js`, { ready: () => !!window.Radzen })
    ]);
    return loading;
}
