#!/usr/bin/env node
// Enforces the NET_RULES 5.1 tier quotas: Unit 100 / Integration 50 / E2EAPI 25 / E2EUI 25.
//
// ── Why this counts METHODS, not cases ──────────────────────────────────────────────────────
// A `[Theory]` is ONE test method however many `[InlineData]` or `[MemberData]` rows it carries,
// and `dotnet test --list-tests` expands those rows into individual cases (the Unit tier's 91
// methods expand to well over 200 cases). The caps were always method-counted: E2EAPI sat at
// exactly 25 and Integration at exactly 51 against a cap of 50, which only lines up under method
// counting. If that definition ever changes, change the docs in the same commit — an enforcement
// script whose unit is ambiguous is worse than none.
//
// ── Why the cap exists ──────────────────────────────────────────────────────────────────────
// A tier that can grow without limit stops being a tier. When a valuable test pushes a suite
// over, the answer is to delete something worth less — not to raise the number. The Unit tier
// was brought to 91 by deleting 34 methods that asserted nothing about production code
// (placeholder stubs and tests that called a private helper defined in the test file).
//
// Usage: node SCRIPTS/check-test-quota.mjs [--json]

import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';

const CAPS = [
    { project: 'PoSeeReview.Unit', cap: 100 },
    { project: 'PoSeeReview.Integration', cap: 50 },
    { project: 'PoSeeReview.E2EAPI', cap: 25 },
    { project: 'PoSeeReview.E2EUI', cap: 25 },
];

const SKIP_DIRS = new Set(['bin', 'obj']);
const TEST_ATTRIBUTE = /^\s*\[(Fact|Theory)\b/gm;

function collectCsFiles(dir) {
    const found = [];
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
        if (entry.isDirectory()) {
            if (!SKIP_DIRS.has(entry.name)) found.push(...collectCsFiles(join(dir, entry.name)));
        } else if (entry.name.endsWith('.cs')) {
            found.push(join(dir, entry.name));
        }
    }
    return found;
}

function countTests(file) {
    const source = readFileSync(file, 'utf8');
    return [...source.matchAll(TEST_ATTRIBUTE)].length;
}

const testsDir = join(process.cwd(), 'tests');
const results = [];
let failed = false;

for (const { project, cap } of CAPS) {
    const dir = join(testsDir, project);
    let count = 0;
    let files = [];
    try {
        files = collectCsFiles(dir);
        count = files.reduce((total, file) => total + countTests(file), 0);
    } catch (error) {
        // A missing tier is a failure, not a skip: silently passing would let a suite be deleted
        // wholesale and still report green.
        results.push({ project, cap, count: null, over: null, error: error.message });
        failed = true;
        continue;
    }

    const over = count - cap;
    if (over > 0) failed = true;
    results.push({ project, cap, count, over: over > 0 ? over : 0 });
}

if (process.argv.includes('--json')) {
    console.log(JSON.stringify(results, null, 2));
} else {
    const width = Math.max(...results.map(r => r.project.length));
    for (const r of results) {
        if (r.count === null) {
            console.error(`  ✗ ${r.project.padEnd(width)}  could not be read: ${r.error}`);
            continue;
        }
        const mark = r.over > 0 ? '✗' : '✓';
        const note = r.over > 0 ? `  OVER BY ${r.over}` : '';
        console.log(`  ${mark} ${r.project.padEnd(width)}  ${String(r.count).padStart(3)} / ${String(r.cap).padStart(3)}${note}`);
    }
}

if (failed) {
    console.error('');
    console.error('Tier quota exceeded (NET_RULES 5.1). Delete tests worth less than the ones you are adding.');
    process.exit(1);
}

console.log('');
console.log('Test quotas OK.');
