# AGENTS.md

## 1. Standard

> This repo owns every file in it. Nothing here is generated from, synced with, or required to stay identical to
> another repository. Headers like "SAME FILE IN EVERY PO …", "Generated from po-shared … DO NOT EDIT", and scripts
> that copy files into or check other repos are forbidden. Only Azure resources in the `PoShared` resource group
> may be shared.

### Po Web App Standard

This repo is one of several independent Po apps. It follows the conventions below, but it owns every file:
nothing is generated from, synced with, or required to stay identical to another repository. Change anything
here freely; only the *conventions* are common, never the code. Azure resources may be shared through the
`PoShared` resource group; code may not.

#### Repo skeleton
```
README.md          what the app is, live URL, how to run locally, how it deploys
AGENTS.md          this standard + repo-specific notes (CLAUDE.md just points here)
CLAUDE.md          one line: "Read AGENTS.md."  (plus repo-specific notes only if unavoidable)
<App>.slnx  global.json  Directory.Build.props  Directory.Packages.props  .editorconfig
src/<App>.Api  src/<App>.Client  src/<App>.Shared  [src/<App>.Mobile]
tests/<App>.Unit  tests/<App>.Integration  [tests/<App>.E2E...]
infra/main.bicep  infra/main.bicepparam  [infra/modules/*.bicep]
scripts/           setup / helper scripts (no infra/*.ps1)
docs/              lowercase; everything else that is prose
.github/workflows/deploy.yml   the ONLY workflow
```
No `.cursorrules`, `.copilotignore`, `SPEC.md`, `tasks/`, `DOCS/` at the root (fold useful content into docs/).
`SCREENSHOTS/` is allowed only if README references it.

#### Page shell (every page, signed in or out, including the sign-in page)
- Header `.po-header`, height 56px, sticky: app name top-left (`.po-header__title`, links to `/`).
- Top-right `.po-header__actions`: signed-in user's **email** (`.po-header__email`, full email, ellipsis at 16ch on
  phones) — or, signed out, one **Sign in** button `class="po-link-btn filled small"` (same style on every page) —
  then the settings gear ONLY if the app has a settings page (the owner removed settings from some apps — don't
  re-add it). Apps without OAuth show nothing in place of the email (no fake identity chips).
- Version badge `.po-version` fixed bottom-right showing `vYYMMDD`. It must never cover content: `main` gets
  `padding-bottom: calc(var(--po-space-6) + env(safe-area-inset-bottom))`, and when a mobile bottom nav exists the
  badge sits above it (`--po-stamp-lift: <nav height>`).
- Colours only from `wwwroot/css/po-tokens.css` (`--po-*`). All text ≥ 4.5:1 contrast in light AND dark mode
  (status banners: use `--po-text` on `--po-*-soft` backgrounds, never warning-on-peach).
- Login-only data must not be requested for signed-out visitors (no 401s in the console on public pages), and
  actions a signed-out visitor cannot perform are hidden.

#### Version
- `Directory.Build.props` stamps ONE assembly metadata key: `BuildVersion` = UTC `yyMMdd`. No `BuildStamp`/`BuildDate`
  aliases. Every reader (header badge component, `/api/diag`, `/health` headers) reads `BuildVersion`.

#### Health + /diag contract
- `GET /health/live` (liveness, returns header `X-Commit: <sha>`), `GET /health` (readiness), `GET /api/diag` →
  ```
  DiagReportDto(string Status, string Environment, string Version, DateTimeOffset CheckedAt, double CheckedMs,
                DiagCheckDto[] Checks)
  DiagCheckDto(string Name, string Group /* "api" | "data" | "service" */, string Label, string Status
               /* Healthy|Degraded|Unhealthy */, string Purpose, string Detail, double LatencyMs, string? Problem)
  ```
- UI route `/diag` ONLY (delete other diagnostics pages/routes such as `/system`, `/diagnostics`, `/status`). Page
  `<title>` and `h1`: **System status**. Layout: overall banner in plain words ("Everything is working" /
  "Working, with N things degraded" / "Not working: …"), then three sections in this order: **API**, **Data**,
  **Services**; each row = label, purpose, status pill, latency ms, problem/detail text. Every external dependency
  the app really calls appears as a check. Anonymous, nothing secret in the payload.

#### Workflow (`.github/workflows/deploy.yml`, the only one)
push to master → format check → build (Release, warnings as errors) → Unit tests → Integration tests (Testcontainers /
Azurite; Docker is available on ubuntu-latest) → publish → deploy → confirm `/health/live` X-Commit = this SHA.
If the repo has a MAUI `src/<App>.Mobile`, a second job in the SAME file (`mobile`, `needs: []`, not blocking deploy)
installs the `maui-android` workload and builds the Android target, so the client is always compiled.

#### Tests
- Unit tier: fast, no I/O, runs in CI.
- Integration tier: runs in CI; locally it SKIPS with a clear message when Docker is not running (never a wall of
  `DockerUnavailableException` failures).
- E2E/UI tiers: run locally against a running app; when no app URL / browser is available they SKIP with a clear
  message. They are not part of CI.
- Docs must state real counts, not caps that are out of date.

#### Azure naming
App Service `app-<repo lowercase>`; per-app resources named `<type>-<repo lowercase>`; shared things only in `PoShared`.

## 2. Concurrent work

> Several sessions (people and agents) commit to this repo, sometimes at the same time.
> - Before every commit and every push: `git fetch origin && git rebase --autostash origin/master`.
> - Stage only the files you changed (`git add <paths>`); never `git add -A` / `git add .`; check `git diff --cached`.
> - Never revert, reformat or overwrite changes you didn't make; keep the owner's recent decisions (e.g. features
>   removed on purpose stay removed).
> - Don't commit or sync from the IDE while an agent is working in this folder.
> - Pushing `master` deploys production; changes to `.github/workflows/*` need a token with the `workflow` scope.

## 3. Verification

Every command below was run on 2026-11-20 (PowerShell or bash, repo root).

| What | Command | Needs / skips |
|---|---|---|
| Format | `dotnet format PoSeeReview.slnx --verify-no-changes` | — |
| Build | `dotnet build PoSeeReview.slnx -c Release` | warnings are errors |
| Unit | `dotnet test tests/PoSeeReview.Unit -c Release` | nothing (224 tests) |
| Integration | `dotnet test tests/PoSeeReview.Integration -c Release` | Docker (Testcontainers Azurite); skips with one message without it (43) |
| E2E API | `dotnet test tests/PoSeeReview.E2EAPI -c Release` | Docker; skips without it (38) |
| E2E UI | `E2E_BASE_URL=http://localhost:7318 dotnet test tests/PoSeeReview.E2EUI -c Release` | a running app + Playwright Chromium; skips with the reason otherwise (42) |
| Azurite for a local run | `docker run -d --rm -p 21000:10000 -p 21001:10001 -p 21002:10002 mcr.microsoft.com/azure-storage/azurite:latest azurite --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0 --skipApiVersionCheck --loose` | Docker |
| Run locally (mock data, no secrets) | `KeyVault__Uri= UseMockData=true Storage__AzuritePorts=21000,21001,21002 ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:7318 dotnet run --project src/PoSeeReview.Api --no-launch-profile` | → http://localhost:7318 and http://localhost:7318/diag |

- **Deploy:** push to `master` runs `.github/workflows/deploy.yml` (format, build, Unit, Integration, publish,
  deploy; the `mobile` job builds the Android client beside it). `*.md` changes do not deploy.
- **Deploy check:** `curl -sI https://app-poseereview2.azurewebsites.net/health/live` shows `X-Commit: <pushed sha>`
  (the workflow waits for it).
- **Live:** https://app-poseereview2.azurewebsites.net and https://app-poseereview2.azurewebsites.net/diag
- **Pinned:** .NET SDK `10.0.400` (`global.json`, rollForward latestFeature); MAUI client built with the
  `maui-android` workload in CI only (not in the .slnx).

---

# PoSeeReview: repo-specific notes

Architecture, commands, configuration and conventions are in
[docs/development.md](docs/development.md); the PRD is [README.md](README.md). The rules below govern
how any coding agent operates in this repo.

## 1. Branching & version control

- **`master` only.** Do all work on `master`. Use another branch only when explicitly asked to.
- **Never push to remote unless asked.** Committing locally is fine; `git push` is not, until the
  user says so — or until they type "git sync".
- **On "git sync": stage everything, commit, push.** Commit *all* outstanding changes first — a
  sync leaves nothing dirty behind. Short, non-technical American-slang message that reads like a
  human wrote it ("fixed the busted nav", "cleaned up that css mess"), then push without asking.

## 2. Runtime, testing & quality

- **Restart and verify after every code change.** Stop the app, start it again
  (`dotnet run --project src/PoSeeReview.Api --launch-profile https`, or the
  `start-api-clean` VS Code task), and confirm it actually came up before reporting done.
  Config/appsettings/Key Vault changes need a full restart — `dotnet watch` will not pick them up.
- **Only run the tests that cover the change.** Pick the project and `--filter` that exercise what
  was touched; for a change with no test surface — a copy tweak, a CSS value — run none at all.
  Never reach for the full suite after a code change.
- **Warnings are errors.** `TreatWarningsAsErrors` is on globally via `Directory.Build.props`; fix
  every warning at its cause, immediately. Never suppress one (`#pragma`, `NoWarn`) to get a build
  green — that includes NuGet audit advisories, which are fixed by upgrading.

## 3. Architecture & configuration

- **Read the docs first, and keep them in sync.** Check [docs/development.md](docs/development.md) and
  [README.md](README.md) for the architecture before exploring the code. Whenever a change adds, removes
  or upgrades a dependency, adds or renames a configuration key, or adds or changes a major
  endpoint, update README.md and docs/ **in the same change**.
- **No `dotnet user-secrets`.** Non-secret config goes in `appsettings*.json`; real secrets go in
  Key Vault `kv-poshared2` under the `PoSeeReview--` prefix. The one existing exception is
  `Takedowns:ApiKey` for local dev — it is a live credential, so it must never land in an
  appsettings file that is committed.
- **Automate everything you can.** Run the commands yourself, and script the steps that repeat.
  Don't hand the user a command to paste or a web portal to click through when the agent can do
  it; only ask when it genuinely needs their machine, credentials, or a decision.
- **The deployed build is stamped `v{YYMMDD}`, bottom-right.** The `PoBuildStamp` target in
  `Directory.Build.props` stamps `BuildVersion` from the HEAD commit's UTC date (today's date without git);
  `BuildVersion.razor` shows it (`<span class="po-version">`) in both layouts and `/api/diag` reports it as `version` (shown on the `/diag` page). Keep it working; don't hand-edit a version.

## 4. UI changes & reporting

- **Screenshot UI changes, before and after.** When a change touches the UI, capture the old and
  new states, annotate what changed, and save it as one self-contained page:
  `SCREENSHOTS/YYMMDD-feature-name.html` (today's date, kebab-case feature), plus a single
  annotated PNG beside it (`SCREENSHOTS/YYMMDD-feature-name.png`) showing before and after side by
  side with the notes drawn on. Give the full, absolute paths to both files in the reply. The
  folder is gitignored (the pages embed their images, and committing them grew the repo by
  megabytes per change): they are local review aids.
- **Call out big deletions.** If a prompt's changes remove more than 100 lines of code overall
  (net), say so in the reply.
- **TL;DR any answer over 100 words** with a recap of under 20 words at the very end.
- **3-tier diff summary for any code change over 20 lines.** Append exactly three lines, one per
  tier:
  1. **Toddler:** one sentence of simple, conceptual intuition.
  2. **Child:** one sentence on what it now does.
  3. **Adult:** one sentence on the technical/architectural change.
- **Direction questions after a big change.** After a code change over 100 lines, and only when the
  prompt left no other questions open, end with 3 optional, high-level questions about where the
  app's purpose is heading — the kind whose answers could trigger further changes.

## CI/CD and Azure

- A push to `master` runs `.github/workflows/deploy.yml` (the shape in the standard above): format check, Release build, Unit and Integration tiers, publish, deploy, then wait for `/health/live` to report the pushed commit in `X-Commit`. A red check stops the deploy and the live site keeps its last good build. The non-blocking `mobile` job builds the Android target of `src/PoSeeReview.Mobile`. E2EAPI, E2EUI, Bicep and the other scripts run locally. Nothing is deployed by hand.
- Azure sign-in is GitHub OIDC (the `gh-<repo>-deploy` app). Its IDs are repo **variables** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`, and the App Service name is `AZURE_WEBAPP_NAME`. Never move them to secrets or write resource names into workflows.
- Every permission the app and CI need lives in `scripts/ensure-azure-access.ps1`. A role granted by hand and not added there is a bug: the next re-provision loses it.
- The app answers `/health/live` (process up, no dependencies, `X-Commit` header) and `/health` (dependencies reachable). The deploy waits for `/health/live` to report the pushed commit.
- Tests pass on any machine: no drive-letter, temp-folder or working-directory assumptions, and no live config value hard-coded as an expected result (read it from the same config the app reads).
- A red run on the default branch gets an ntfy push from the CI watcher in PoPunkouterSoftware. Fix it, or say why it is red.
