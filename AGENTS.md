# AGENTS.md

Working rules (NET_AGENTS). These govern how any coding agent operates in this repo, not how the
code is written — for architecture and conventions see [CLAUDE.md](CLAUDE.md) and
[README.md](README.md).

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

- **Read the docs first, and keep them in sync.** Check a root `DOCS/` (or `docs/`) folder and
  [README.md](README.md) for the architecture before exploring the code. `DOCS/` does not exist
  right now — the generated reports were cleared out and are due to be rebuilt — so README.md (the
  PRD) and [CLAUDE.md](CLAUDE.md) are the authoritative overview. Whenever a change adds, removes
  or upgrades a dependency, adds or renames a configuration key, or adds or changes a major
  endpoint, update README.md (and `DOCS/` once it exists) **in the same change**.
- **No `dotnet user-secrets`.** Non-secret config goes in `appsettings*.json`; real secrets go in
  Key Vault `kv-poshared` under the `PoSeeReview--` prefix. The one existing exception is
  `Takedowns:ApiKey` for local dev — it is a live credential, so it must never land in an
  appsettings file that is committed.
- **Automate everything you can.** Run the commands yourself, and script the steps that repeat.
  Don't hand the user a command to paste or a web portal to click through when the agent can do
  it; only ask when it genuinely needs their machine, credentials, or a decision.
- **The deployed build is stamped `YYMMDD`, bottom-right.** The `StampBuildDate` target in
  `Directory.Build.targets` derives it from the HEAD commit's UTC date (today's date without git);
  `BuildStamp.razor` shows it in both layouts and `/diag` reports it as `buildStamp`. Keep it working; don't hand-edit a version.

## 4. UI changes & reporting

- **Screenshot UI changes, before and after.** When a change touches the UI, capture the old and
  new states, annotate what changed, and save it as one self-contained page:
  `SCREENSHOTS/YYMMDD-feature-name.html` (today's date, kebab-case feature). Give the full,
  absolute path to that file in the reply. The folder is gitignored (the pages embed their images,
  and committing them grew the repo by megabytes per change): they are local review aids.
- **Call out big deletions.** If a prompt's changes remove more than 100 lines of code overall
  (net), say so in the reply.
- **TL;DR any answer over 100 words** with a concise ~20-word summary at the end.
- **3-tier diff summary for any code change over 20 lines.** Append exactly three lines, one per
  tier:
  1. **Toddler:** one sentence of simple, conceptual intuition.
  2. **Child:** one sentence on what it now does.
  3. **Adult:** one sentence on the technical/architectural change.
- **Direction questions after a big change.** After a code change over 100 lines, and only when the
  prompt left no other questions open, end with 3 optional, high-level questions about where the
  app's purpose is heading — the kind whose answers could trigger further changes.
