# PoSeeReview

PoSeeReview turns unusual restaurant reviews into AI-generated four-panel comic strips and publishes ranked results to a global Hall of Fame leaderboard. The solution runs as a Blazor WebAssembly frontend and .NET 10 API in a single Azure App Service, backed by Azure Table and Blob Storage, Azure OpenAI, and Google Gemini for narrative and image generation.

---

## Product Requirements Document (PRD)

### Vision

Every neighborhood has restaurants with bizarre, unhinged, or strangely poetic Google Maps reviews. PoSeeReview mines that raw material, distills the strangest signal from real reviews, and turns it into shareable four-panel comic strips. The output is ranked on a global leaderboard by a computed "strangeness score" (0–100), creating a continuously refreshing artifact of collective absurdity.

The product has a single core loop: open app → share location → browse nearby restaurants → tap one → watch the AI pipeline run → receive a personalized comic strip → share it or check your restaurant's global rank. This loop must complete in under 12 seconds on a cold miss, and under 500ms on a cache hit.

### Problem

Restaurant review platforms index enormous volumes of user-generated content that is simultaneously hyper-local and culturally rich. Yet none of it is presented in a format optimized for entertainment, sharing, or rediscovery. Reviews are read once and forgotten. PoSeeReview recovers this latent value and transforms it into a new, ephemeral medium — the comic strip — that is inherently shareable and globally comparable.

The problem is not a lack of content; it is a lack of a lens that makes that content arresting. PoSeeReview is that lens.

### Target Users

**Primary persona — Curious Local:** A smartphone user who opens the app while waiting for food, while exploring a new neighborhood, or while bored. They want to be entertained by something real and local. They are not looking for utility — they are looking for surprise. The friction to the first surprise must be near-zero. No registration, no onboarding form, no paywall.

**Secondary persona — Restaurant Obsessive:** A food blogger, local guide contributor, or review-reading enthusiast who already consumes review content as entertainment. They engage deeply with the strangeness score and leaderboard, returning to see if their favorite weird restaurant has risen in global rank.

**Tertiary persona — Content Creator:** A person who shares the comic to social media. They need the share action to work with one tap and to produce a clean, standalone image.

### Core Features

**1. Proximity-first discovery.** The user grants location access and immediately sees a grid of restaurants within 5 km, sorted by distance. No search box, no category filters — the intent is serendipity, not utility. Manual location search is available as a fallback when geolocation is denied.

**2. One-tap comic generation.** A single tap on a restaurant card initiates the AI pipeline. The loading state is presented as numbered steps (Fetching reviews → Analyzing strangeness → Writing narrative → Generating artwork → Composing strip) so users understand the system is working rather than broken. The generation target is under 10 seconds.

**3. Strangeness scoring.** Azure OpenAI (`gpt-5.4-nano`, temperature 0.3) reads up to five reviews, lowest-rated first, and returns three 0–10 ratings (absurdity, specificity, story potential), a narrative and a 1–2 panel script; the code computes the 0–100 score. Every panel cites the review numbers it draws on (`reviewIds`); citations outside the reviews sent are dropped. The verdict is cached per place in `PoSeeReviewAnalyses`, keyed by a hash of the reviews and `Comics:PromptVersion`, so a redraw keeps its score and a "too ordinary" place is never re-scored for nothing. The system message is kept above Azure's 1024-token prompt-cache threshold (measured: 1024 of ~1265 input tokens cached). Places that score below `Comics:MinimumStrangenessScore` get a 422 and a refund.

**4. Comic image generation — draft, then remaster.** Every comic is drawn first by the cheap painter (`Ai:ImageProvider`, default `AzureOpenAI` = `gpt-image-1-mini` at low quality, ~$0.005). `ComicRemasterService` redraws the live board's top `Remaster:TopN` with the premium painter (`Ai:RemasterProvider`, default `Gemini` = `gemini-2.5-flash-image`, ~$0.04) from the same scenes and captions, at most `Remaster:MaxPerRun` every `Remaster:IntervalMinutes`; the score never moves, the blob is overwritten in place so board and archive rows follow, and a refused or failed remaster is recorded and never retried. `ComicTextOverlayService` draws the captions. A sampled vision check (`ImageQa:SampleRate`, `ImageQa:Deployment`) scores panel count, stray lettering, scene match and character consistency per image model (`Comics.ImageQa.*`) — it measures, never redraws. `Ai:ImageProvider=Local` (Development only) draws through a local OpenAI-compatible server (`LocalImage:*`) so image-prompt work costs nothing.

**4a. Weirdness forecast and pre-draw.** `GET /api/comics/cached` also returns `forecasts` — the cached score for undrawn places — and `forecastFloor`; discovery greys out places already known to be too ordinary. Nothing is computed for the probe. Each forecast sighting and each comic-page miss counts as demand; `ComicPredrawService` draws, once a day, up to `Predraw:MaxPerRun` places scoring at least `Predraw:MinScore` with `Predraw:MinDemand` sightings and no live comic, zeroing their demand before each attempt so a failure is not retried nightly.

**1a. Map-first discovery.** Results open on a map (List is the remembered alternative; devices without WebGL fall back to the list on their own). Pan or zoom and a **Search this area** button re-runs nearby search at the map's centre without moving the camera. Once results are showing, the search bar turns compact and carries the query (edit it and search again), a clear button, the count and the List / Map switch; its **Near me** locates the user, draws a "you are here" dot and, with the map up, searches around it without leaving the map. On a phone the three nearest drawable places sit under the map as cards ("Nearest to you", or "In this area" for a panned or typed search, where distances are hidden because they are not from the user) with a link to the full list; from 64rem the map and every drawable card sit side by side, the cards scrolling beside the map, and a tapped pin's card moves to the top. The landing pairs the search with what is already drawn — the live top five and this browser's recent comics — in two columns on a desktop. The live Hall of Fame folds its scope and layout into one switch: Right now · By week · Map. Pins are coloured and numbered by the live comic's score (`GET /api/comics/cached` returns `scores` alongside `cachedPlaceIds`); an undrawn place is a small dashed pin. A pin tap only selects — the place's usual card or row appears under the map, so opening or paying for a comic is still that card's own button. Leaderboard rows carry `latitude` / `longitude` from the comic that wrote them; rows older than that have none and the board's toggle stays hidden until some do.

**5. Global leaderboard.** Every generated comic is projected to a regional leaderboard (`PoSeeReviewLeaderboard` table) using an inverted RowKey pattern for zero-cost descending sort. The leaderboard is queryable by ISO region code (e.g., `US`, `GB`, `US-CA-SF`) and returns the top N entries sorted by strangeness descending. The live board only lists places whose comic would open for free right now — unexpired and drawn under the current `Comics:PromptVersion` (`ILiveComicLookup`, owned by Comics) — so a row never leads to the paid Generate prompt; when a comic expires the cleanup also deletes its live board row, keeping the blob for the weekly archive.

**6. Content takedown.** Restaurant owners or legal representatives can request removal of a specific comic via `POST /api/takedowns` authenticated with an `X-Api-Key` header. The controller atomically deletes the comic table row, the blob asset, and the leaderboard entry. Telemetry events are emitted for moderation audit trails.

**6a. AI-assisted moderation.** The pre-publish screen layers Azure AI Content Safety (`Moderation:ContentSafetyEndpoint`, the shared AI Services resource and the scorer's key) under the lexical word lists; it fails open to the lexical verdict. `POST /api/moderation/{placeId}/triage` (Moderator role) asks `Moderation:TriageDeployment` (`gpt-5.4-mini`) to compare the live comic with its reviews and reports and suggest keep / hide / remove with reasons; the advice is stored beside the queue row and shown on `/moderation`. It never changes state.

**7. Ephemeral by design.** Comics expire after 24 hours. The `ExpiredComicCleanupService` runs on a 30-minute background loop and batch-purges expired rows and blobs (batch size 25, configurable 5–200). This keeps storage costs bounded and the leaderboard fresh. Users who want to regenerate a comic can force-refresh at any time.

### Non-Goals (v1)

- No user accounts, saved comics, or personalized history.
- No automated moderation decisions: the AI triage advises, a moderator acts.
- No real-time collaborative features beyond optimistic concurrency on the same restaurant.
- No mobile native app — Blazor WASM in the browser is the delivery target.
- No paid tier or premium features.

### Technical Constraints

- **Latency target:** p50 cold-miss generation < 10s. p95 cache hit < 500ms.
- **Caching:** Comics and restaurant data cache for 24 hours in Azure Table Storage. SAS URLs are issued for 8 days to avoid stale URL errors within the cache window.
- **Rate limiting:** The comic generation endpoint (`POST /api/comics/{placeId}`) is protected by a named rate limit policy to prevent quota exhaustion on external AI providers.
- **Bot protection:** All requests are evaluated by `UserAgentValidationMiddleware`. Requests with missing or blocked User-Agent patterns are rejected at 400 before hitting the application layer.
- **No authentication required for read operations.** All read endpoints (nearby restaurants, leaderboard, comic by ID) are public. Generation endpoints are public but rate-limited. Only `POST /api/takedowns` requires a credential.
- **Security:** All secrets are stored in Azure Key Vault and resolved at startup via Managed Identity. No credentials appear in `appsettings.json` or environment variables in production.

### Success Metrics

- Time-to-first-comic for a new user < 30 seconds end-to-end.
- Cache hit rate > 40% for restaurants in active urban areas.
- Zero 5xx errors due to unhandled exceptions (all paths covered by `GlobalExceptionHandler`).
- Leaderboard latency < 200ms p99.
- Takedown SLA < 5 minutes from submission to artifact removal.

---

## Architecture Overview

| Layer | Technologies |
|---|---|
| Edge Delivery | Browser (Blazor WASM), Azure App Service host |
| Compute | ASP.NET Core 10 API, `ExpiredComicCleanupService`, `ComicRemasterService`, `ComicPredrawService`, `ComicImageQaService` |
| Data | Azure Table Storage, Azure Blob Storage, Azure Key Vault |
| External AI | Azure OpenAI (`gpt-5.4-nano` scorer and image QA, `gpt-5.4-mini` moderation triage, `gpt-image-1-mini` drafts), Azure AI Content Safety, Google Gemini (`gemini-2.5-flash-image` remasters), Google Maps Places. Local: Ollama (chat), any OpenAI-compatible image server (Development) |
| Observability | Application Insights, OpenTelemetry, Serilog |
| Imaging | SkiaSharp (caption overlay, lossy WebP, share and duel cards); Bangers and DM Sans embedded |
| Operator UI | Radzen.Blazor 12.0.6 on `/moderation` (DataGrid, confirm dialog) and `/diagnostics` (tabs) only, through `RadzenScope`; its `material-base` theme is fetched into the `vendor` cascade layer and re-coloured from the app's tokens by the `--rz-*` bridge in `app.css`. The trimmed assembly and the framework pieces it pulls in add ~0.97 MB brotli to the boot download |
| Graphics & audio | One hand-written WebGL2 backdrop shader on a shared rAF loop with a frame-budget tier (`off`/`lite`/`full`, `lite` by default); an optional WebGPU compute particle layer (ink motes); 2D-canvas confetti and ink splats; Web Audio synthesis, no audio assets, off by default. Every optional extra (sound, haptics, soundtrack, map sounds, ink motes, effects tier) is switched on `/settings` |
| Maps | MapLibre GL JS 5.24.0 (UMD, jsDelivr, SRI-pinned, loaded only when a map opens) over OpenFreeMap vector tiles — no key, no per-load billing |
| Alerting | `infra/alerts.bicep`: Http5xx, AI spend, in-band generation failures, fail-open guards, image refusals, moderation queue → email action group |
| Quality gates | axe-core (`Deque.AxeCore.Playwright` 4.13.0) in the E2E UI suite; Lighthouse CI (`@lhci/cli` 0.15.1, `SCRIPTS/lighthouserc.json`) in the deploy smoke job |
| Build stamp | `YYMMDD` from the HEAD commit date (`Directory.Build.targets`), shown bottom-right on every page and as `buildStamp` on `/diag` |

## Documentation Suite

All diagrams are self-contained HTML files rendered by Mermaid.js with the cathrynlavery/diagram-design aesthetic. Open [docs/index.html](./docs/index.html) for the full gallery.

### 1 — Architecture & CI/CD Strategy

| Diagram | Full | Simple |
|---|---|---|
| Architecture Master (C4 Hybrid L1/L2) | [docs/Architecture_MASTER.html](./docs/Architecture_MASTER.html) | [docs/Architecture_MASTER_SIMPLE.html](./docs/Architecture_MASTER_SIMPLE.html) |
| Release Pipeline Master | [docs/ReleasePipeline_MASTER.html](./docs/ReleasePipeline_MASTER.html) | [docs/ReleasePipeline_MASTER_SIMPLE.html](./docs/ReleasePipeline_MASTER_SIMPLE.html) |

### 2 — User Usage & Behavioral Flowcharts

| Diagram | Full | Simple |
|---|---|---|
| Onboarding Journey | [docs/OnboardingJourney.html](./docs/OnboardingJourney.html) | [docs/OnboardingJourney_SIMPLE.html](./docs/OnboardingJourney_SIMPLE.html) |
| Primary Value Flow (Happy Path) | [docs/PrimaryValueFlow.html](./docs/PrimaryValueFlow.html) | [docs/PrimaryValueFlow_SIMPLE.html](./docs/PrimaryValueFlow_SIMPLE.html) |
| Exception User Flows | [docs/ExceptionUserFlows.html](./docs/ExceptionUserFlows.html) | [docs/ExceptionUserFlows_SIMPLE.html](./docs/ExceptionUserFlows_SIMPLE.html) |

### 3 — Logic & State Dynamics

| Diagram | Full | Simple |
|---|---|---|
| System Flow Master | [docs/SystemFlow_MASTER.html](./docs/SystemFlow_MASTER.html) | [docs/SystemFlow_MASTER_SIMPLE.html](./docs/SystemFlow_MASTER_SIMPLE.html) |
| State Dynamics Master | [docs/StateDynamics_MASTER.html](./docs/StateDynamics_MASTER.html) | [docs/StateDynamics_MASTER_SIMPLE.html](./docs/StateDynamics_MASTER_SIMPLE.html) |

### 4 — Data & Security Schema

| Diagram | Full | Simple |
|---|---|---|
| Data Model (ERD) | [docs/DataModel.html](./docs/DataModel.html) | [docs/DataModel_SIMPLE.html](./docs/DataModel_SIMPLE.html) |
| Access Control Matrix | [docs/AccessControl_MATRIX.html](./docs/AccessControl_MATRIX.html) | [docs/AccessControl_MATRIX_SIMPLE.html](./docs/AccessControl_MATRIX_SIMPLE.html) |
| Data Lifecycle Master | [docs/DataLifecycle_MASTER.html](./docs/DataLifecycle_MASTER.html) | [docs/DataLifecycle_MASTER_SIMPLE.html](./docs/DataLifecycle_MASTER_SIMPLE.html) |

### 5 — Dependency & UI Hierarchy

| Diagram | Full | Simple |
|---|---|---|
| System Interaction Flow (Sequence) | [docs/SystemInteractionFlow.html](./docs/SystemInteractionFlow.html) | [docs/SystemInteractionFlow_SIMPLE.html](./docs/SystemInteractionFlow_SIMPLE.html) |
| Service Map Master | [docs/ServiceMap_MASTER.html](./docs/ServiceMap_MASTER.html) | [docs/ServiceMap_MASTER_SIMPLE.html](./docs/ServiceMap_MASTER_SIMPLE.html) |
| Interface Hierarchy Master | [docs/InterfaceHierarchy_MASTER.html](./docs/InterfaceHierarchy_MASTER.html) | [docs/InterfaceHierarchy_MASTER_SIMPLE.html](./docs/InterfaceHierarchy_MASTER_SIMPLE.html) |

### Visual assets

- [docs/screenshots](./docs/screenshots) — reserved for UI screenshots and flow captures.

## Refactor Blast Radius Assessment

### Refactor: Replace direct provider clients with a single orchestration service

- Purpose: centralize Google Maps, LLM, and image-model execution with explicit retries, fallback policy, and correlation IDs.
- Blast radius:
  - API layer: changes to service registration and endpoint call paths.
  - Infrastructure layer: adapter wrappers around existing provider SDK clients.
  - Telemetry: metric names and traces may shift to orchestration-centric spans.
  - Tests: unit/integration tests mocking provider dependencies must be updated.
  - Downstream dependencies: provider quota behavior and timeout strategy become coupled to orchestrator policy.

### Refactor: Introduce projection worker for leaderboard materialization

- Purpose: decouple ranking updates from request path to reduce p95 latency.
- Blast radius:
  - API layer: request handlers move from synchronous projection writes to enqueue-only behavior.
  - Data layer: adds queue/table checkpoint records and idempotency keys.
  - UI layer: leaderboard eventual-consistency window must be communicated.
  - Operations: requires new health checks and backlog alerts.
  - Downstream dependencies: consumers reading immediate ranks may observe delayed updates.

### Refactor: Add versioned state machine for comic lifecycle

- Purpose: make status transitions explicit and enforceable across create, publish, expire, and takedown flows.
- Blast radius:
  - Core model: enum/state transition rules and validation constraints change.
  - API contracts: response fields may include state version and transition metadata.
  - Storage schema: migration required for lifecycle version and transition audit fields.
  - Background jobs: cleanup/takedown workers must honor state-machine gates.
  - Downstream dependencies: reporting and moderation tools must map to new state values.

### Refactor: Introduce anti-corruption DTO boundary between API and shared client contracts

- Purpose: reduce accidental coupling between persistence entities and external payloads.
- Blast radius:
  - API endpoints: mapping logic added for read/write DTO translations.
  - Client app: contract changes may require adapter updates in typed services.
  - Tests: snapshot and serialization tests need baseline refresh.
  - Performance: additional mapping allocations may require profiling.
  - Downstream dependencies: any external consumer of existing DTO shape may need migration coordination.

## Build and Run

```powershell
dotnet restore
dotnet build PoSeeReview.sln
dotnet run --project src/PoSeeReview.Api --launch-profile https
```

### Hot Reload (code-only changes)

Configuration changes (appsettings, Key Vault secrets) require a full API restart. For code-only changes use hot reload to avoid the full startup delay:

```powershell
dotnet watch --project src/PoSeeReview.Api --launch-profile https
```

## Cloud Smoke Testing

After a deploy (push to `master`), the live URL is:

```powershell
az webapp show --name app-poseereview --resource-group PoSeeReview --query defaultHostName -o tsv
```

The same post-deploy checks CI runs (Blazor render tree, `/health`) can be run by hand:

```powershell
# Post-deploy smoke (NET_RULES 5.4) — the deploy workflow runs this automatically
$env:BASE_URL = "https://<fqdn>"
node SCRIPTS/post-deploy-smoke.mjs
```

CI then runs Lighthouse against `/login` (three runs, median). Layout shift above 0.1 or an accessibility score under 0.9 fails the run; performance and LCP only warn, because an F1 cold start swings them. Reports are uploaded as the `lighthouse` artifact.

```powershell
npx @lhci/cli@0.15.1 autorun --config=SCRIPTS/lighthouserc.json --collect.url="https://<fqdn>/login"
```

The full C# Playwright UI suite targets a running app via `E2E_BASE_URL` (it includes `AccessibilityUiTests`, axe-core over the main pages):

```powershell
$env:E2E_BASE_URL = "https://<fqdn>"
dotnet test tests/PoSeeReview.E2EUI
```

