# Edge-headers cleanup plan

## Why

Today's deploy left a working home-card render but a stack of follow-on bugs surfaced by review:

1. **Visitor-detail page radar is broken** — `_BehavioralEvolution.cshtml` renders an empty 12-axis session-vector clock. The session-vector path was deliberately abandoned earlier in the session; this partial is now both architecturally wrong (should be the 7-bucket fingerprint centroid) and empty (no data flows into `clockAxes` any more on the remote-mode path).
2. **Priority race** — `FingerprintMatchContributor.Priority` was changed to `1` to force the matcher to run in Wave 0. `SignatureContributor` is also Priority 1. They race on `state.Signals[SignalKeys.PrimarySignature]` — last-writer-wins. The matcher's synthetic-fallback path writes a wrong signature back to state, SignatureContributor then overwrites with the real one, and the fingerprint row keyed off the synthetic is orphaned. Silent identity churn.
3. **`StyloBotForwardedHeadersMiddleware` opens a SQLite connection per request** to do an L1 lookup. Violates the "no in-memory stores, all DB access through LFU write-/read-through cache" rule.
4. **Hydrator only reads two headers** — `IdentityFingerprint` + `PrimarySignature`. The other five emitted (`Probability`, `Confidence`, `RiskBand`, `BotName`, `Result`, `RequestId`) are paid for over the wire and discarded by the consumer. Verdict signals don't reach view components / tag helpers via `HttpContext.Items`.
5. **Skip path silently downgrades signatures** — `cachedEvidence.Signals` is empty, `precomputedSig` is right there but never written. `DetectionDataExtractor` falls back to a synthesised IP+UA hash, so every verdict-cached request looks like "data is missing" instead of "verdict was cached."
6. **`state.WriteSignal()` writes to `Signals` only, never `Items`**. Multiple consumers check Items first; on the orchestrator path they miss `IdentityFingerprintId` / `PrimarySignature`. Symptom that drove the three-fallback chain in `StyloBotForwardedHeadersMiddleware`; root cause not addressed.
7. **`LogInformation` on every request** in the hydrator — production log spam + PII risk (identity in plaintext logs).
8. **Header-name inconsistency** — `BotDetectionMiddleware.cs:394,398` emits `X-StyloBot-VerdictSource` / `X-StyloBot-IdentityFingerprint` on the *response*. `StyloBotEdgeHeaderNames` defines `X-Bot-Detection-IdentityFingerprint` on the *forwarded request*. Two names for the same field, two code paths.
9. **`EmitOnResponseToClient` toggle missing** — the original spec called for three booleans (strip-inbound, emit-on-forwarded-request, emit-on-response-to-client). Only the first two shipped.
10. **`HeaderPrefix` not configurable** — header names are const strings in `StyloBotEdgeHeaderNames`. Operators can't change the prefix; if two stylobot gateways ever proxy in series they collide.
11. **Service-location anti-pattern** — `StyloBotForwardedHeadersMiddleware` resolves `IFingerprintReader` via `context.RequestServices.GetService<>` per request. Should be ctor-injected (and through the LFU cache, per the rule).
12. **Hardcoded API key in compose** — inline string `staging-test-website-key-do-not-use-elsewhere` in `docker-compose.test.yml`. Pattern for `POSTGRES_PASSWORD` (`${VAR:?...}`) is right there.
13. **Helper duplication** — `Items.TryGetValue(AggregatedEvidenceKey, out var ev) && ev is AggregatedEvidence` repeated three times in one method with different out-var names.
14. **Redundant `ContainsKey` guard** in `StyloBotInboundClientHeaderStripperMiddleware` — `Headers.Remove(name)` is idempotent.

## Architecture rules (non-negotiable, apply to every step below)

Based on the `Mostlylucid.Atoms` / `Mostlylucid.Ephemeral` signals-and-atoms pattern (see `mostlylucid.atoms/mostlylucid.ephemeral/SIGNALS_PATTERN.md`).

### Storage / DB

- **No new in-memory stores. No bare DB access from middleware or view components.** Every DB read/write goes through an LFU write-through + read-through cache layer. The website never accesses any DB directly.
- The cache layer for fingerprint reads is a new atom-style component that wraps `SqliteFingerprintStore` on the gateway; it is the only path middlewares/view-components see for `IFingerprintReader`.
- Reads: cache hit → return; miss → fetch + cache; LFU evicts cold entries.
- Writes: cache write → return; async flush to DB; LFU evicts after flush.

### Signals vs logs

- **Signals are the runtime adaptation/inspection mechanism. Logs are NOT.** StyloFlow adapts on signals; the dashboard reads signals; the LFU sliding cache holds them just long enough to be useful and evicts cold ones. If a piece of code wants to capture something about a request, it writes a signal — not an `ILogger` call.
- **Signals are cheap.** The LFU handles housekeeping. Useful signals stay warm because consumers query them; unused signals evict. Don't pre-emptively constrain signal creation — constrain log creation.
- **Atoms hold state. Signals announce that the atom changed.** Per the pattern: `_sink.Raise("file.saved")`; listeners then query `fileAtom.GetLastFilename()`. State is NOT carried in signal payloads (Model 1, default). Model 2 (`"file.saved:report.pdf"`) is allowed as a fast-path hint but the atom is still queried for truth.
- **`ILogger` is for genuine error/exception conditions and structured event emission only.** No `LogInformation("middleware saw X")` — that's a signal. No `LogDebug("got header Y")` — that's a signal. The reflex to add a log is the bug.

### The Blackboard is a specialization of SignalSink

- `BlackboardState.Signals` is the orchestrator's SignalSink (wave/quorum-aware). `state.WriteSignal(SignalKeys.X, value)` is structurally `_sink.Raise("X", value)`.
- `HttpContext.Items` is the post-orchestrator request-scoped atom-equivalent — the boundary atom that view components / downstream middleware query.
- The Items↔Signals mirror at the end of `PopulateContextFromAggregated` (Section D) is **the atom hand-off across the orchestrator boundary**, not duplicated state-keeping. Inside the orchestrator: SignalSink. Outside: atom on HttpContext. One side raises, the other reads.
- **Contributors are listeners + raisers.** They raise notifications that announce the atom changed; they READ truth by querying the actual atom (a service, a store), NOT by reading the signal payload. The matcher should not bind its fingerprint lookup to a synthetic value that came out of the signal sink — that conflates notification with state and creates the race documented below.

### Practical implications for this plan

- Section E: delete the hydrator's `LogInformation`. If "did hydration happen" is useful to downstream adaptation, emit a signal (`_sink.Raise("identity.hydrated.from-headers")`) and rely on the LFU to keep it warm if consumers care. Don't speculatively add the signal either — only when a consumer asks for it. The hydrator's existing output (writing `IdentityFingerprintId` / `PrimarySignature` into the request-scoped atom = `HttpContext.Items`) IS the observable effect.
- Section A: the `CachedFingerprintReader` is an atom (state + query accessors + invalidate-on-write). It implements `IFingerprintReader` and IS the only thing middlewares see.
- Section D: the Items↔Signals mirror enforces "after detection runs, the atom (HttpContext.Items as request-scoped) holds the load-bearing identity keys." Downstream code queries the atom for truth.
- Sections B, C, F, G, H, I, J: zero new log calls. If a section is tempted to add one, that's a signal-shaped need wearing a log disguise — write the signal.

## Scope

### Section A — Drop the per-request SQLite connection; refactor `SqliteFingerprintStore` to compose `SqliteSingleWriter` + `SqliteConnectionFactory`

**Architectural correction (discovered during exploration):** the original "build a `CachedFingerprintReader` wrapper" approach was the wrong shape. The right primitives live in `mostlylucid.atoms` / `mostlylucid.ephemeral` — which we own and can ship. Two atoms compose to give us what we need:

- `SqliteConnectionFactory` — vends ready-to-use connections; takes a list of `ISqliteConnectionCapability` for per-connection prep (vec0 extension load, WAL pragma, FTS5 tokenizer registration, etc.). Each capability is itself a tiny atom that raises `capability.applied:{name}` / `capability.failed:{name}:{reason}` signals.
- `SqliteSingleWriter` — composes a `SqliteConnectionFactory` with serialized writes (`EphemeralWorkCoordinator(MaxConcurrency=1)`) + cached reads (`EphemeralLruCache` with hot-key extension) + atomic write-and-invalidate + cross-process invalidation via shared `SignalSink`. Knows nothing about extensions, pragmas, or any per-connection setup — that lives in capabilities.

**Why composition not a hook:** capabilities compose, signal-emit, and split "what's a ready connection?" from "how do I coordinate writes?" into two atoms. A hook is the asp.net reflex of bolting a callback onto an API that doesn't want to know.

**See:** Campaign 2 Section 2.1 in `2026-05-28-stabilisation-campaign.md` for the full design and the package-publishing plan. This Section A is the pilot — `SqliteFingerprintStore` is the canary migration, the other 11 stylobot SQLite stores follow mechanically in Campaign 3.

**Files:**
- `src/Mostlylucid.BotDetection/Identity/SqliteVecCapability.cs` (new) — implements `ISqliteConnectionCapability`; loads sqlite-vec via `conn.EnableExtensions(true); conn.LoadExtension("vec0");`.
- `src/Mostlylucid.BotDetection/Identity/SqliteFingerprintStore.cs` — every `new SqliteConnection`/`OpenAsync` site (15+) replaced with `_writer.ReadAsync("cache-key", (conn, ct) => ...)` or `_writer.WriteAndInvalidateAsync(sql, params, cacheKeys)`. The bespoke `OpenConnectionWithVecAsync` method goes away — vec0 prep is now in `SqliteVecCapability` and the factory applies it on every acquired connection.
- `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs:686-691` — DI builds `SqliteConnectionFactory(connStr, [new SqliteVecCapability()])` once, passes to `SqliteSingleWriter` ctor, passes to `SqliteFingerprintStore`. The `IFingerprintReader` registration unchanged.

**Cache keys:** see Campaign 2 Section 2.1 for the convention. Same key scheme; same invalidation matrix.

**Cascading change:** `StyloBotForwardedHeadersMiddleware`'s L1-lookup code path automatically improves — no per-site refactor, the store is now LFU-cached internally.

**Tests:** existing `SqliteFingerprintStore` test coverage should pass unchanged. The refactor is mechanical; the public API of the store is identical. `FingerprintMatcherConvergenceTests` 5/5 must remain green (they hit the store via the matcher).

**Prerequisite:** `mostlylucid.atoms` ships the `Mostlylucid.Ephemeral.Sqlite` package (or sibling) containing `SqliteConnectionFactory` + `ISqliteConnectionCapability` + `SqliteSingleWriter`. Stylobot then takes a `PackageReference` to it.

### Section B — Priority race fix (no synthetic-signature broadcast)

**Goal:** `FingerprintMatchContributor` runs in Wave 0 alongside `SignatureContributor` without overwriting each other.

**Change in `FingerprintMatchContributor.ContributeCoreAsync`:** the synthetic IP+UA fallback signature stays *local to the contributor* — does not call `state.WriteSignal(SignalKeys.PrimarySignature, primarySig)`. Other contributors keep seeing whatever SignatureContributor wrote.

**Change:** if `state.Signals[PrimarySignature]` arrives empty (true race lose), the matcher's synthetic primarySig is used to insert the `fingerprint_keys` row — but BOTH the synthetic key AND, on completion, a second `UpsertKey` against the real signature once available (read from `context.Items[SignalKeys.PrimarySignature]` which the middleware writes pre-orchestrator). The matcher's existing correction path already supports re-pointing keys; reuse it.

**Test:** `FingerprintMatcherConvergenceTests.MatcherRacesSignatureContributor_DoesNotOverwriteSignal` — pre-set `state.Signals[PrimarySignature]` to a known value; run matcher; assert state.Signals[PrimarySignature] is unchanged.

### Section C — Skip-path Items/Signals population

**Goal:** verdict-cache skip path produces a fully-populated `cachedEvidence` so downstream sees the same data shape as full-pipeline requests.

**Change in `BotDetectionMiddleware.cs` skip path (`:379-403`):**
- `cachedEvidence.Signals[SignalKeys.PrimarySignature] = precomputedSig`
- If `v.IdentityFingerprintId is { } fp`, also `cachedEvidence.Signals[SignalKeys.IdentityFingerprintId] = fp`
- Mirror both into `context.Items` (Items↔Signals contract — see Section D)
- If `v.IdentityFingerprintId is null` but `precomputedSig` exists, query the cached fingerprint reader for the id; populate if found.

### Section D — Signals↔Items mirror contract

**Goal:** one canonical contract for "load-bearing identity keys after detection runs."

**Change:** at the end of `BotDetectionMiddleware.PopulateContextFromAggregated`, mirror `IdentityFingerprintId`, `PrimarySignature`, `IpSignature`, `UaSignature` from `aggregated.Signals` to `context.Items`. Code comment documents the contract.

**Cascading:** `StyloBotForwardedHeadersMiddleware` drops the three-fallback chain — reads Items directly and trusts they're populated by the mirror. The fallback was compensating for the missing contract; with the contract there it's redundant.

### Section E — Hydrator: full verdict signal coverage

**Goal:** if the gateway emitted it, the hydrator picks it up. No discarded headers. No log lines.

**Change in `StyloBotForwardedHeadersHydratorMiddleware.cs`:**
- Read all 10 emitted headers (`IdentityFingerprint`, `PrimarySignature`, `IpSignature`, `UaSignature`, `Probability`, `Confidence`, `RiskBand`, `BotName`, `Result`, `RequestId`).
- Hydrate `HttpContext.Items[SignalKeys.IdentityFingerprintId / PrimarySignature]`.
- Construct a stub `AggregatedEvidence` from the verdict headers and put it in `HttpContext.Items[BotDetectionMiddleware.AggregatedEvidenceKey]` so `DetectionDataExtractor.TryExtractFromContextItems` finds it (no longer needs the parallel `TryExtractFromYarpHeaders` path for these signals).
- **Delete the `_logger.LogInformation("StyloBot forwarded-headers hydrator: ...")` call entirely. Drop the `ILogger` ctor parameter and field.** Per the signals-not-logs architecture rule, the hydrator's observable effect IS the atom (its writes to `HttpContext.Items`). Downstream consumers already read those. If a future consumer needs to react to "hydration happened" as an event, that's the moment to emit a signal — not now, speculatively.

### Section F — Visitor-detail page fingerprint radar

**Goal:** the `/dashboard/signature/{id}` page renders the 7-bucket fingerprint centroid radar (same as the home card), not the 12-axis session-vector clock. Both surfaces share one partial.

**Changes:**
- `Models/DashboardPartialModels.cs:293` (`SignatureDetailModel`) gains `Services.FingerprintRadarShape? FingerprintShape { get; init; }`.
- `StyloBotDashboardMiddleware.cs:4329` + `:4443` (the two `new SignatureDetailModel { ... }` sites) populate `FingerprintShape` by: cached `IFingerprintReader.LookupFingerprintIdAsync(primarySig)` → `GetFingerprintAsync(fpId)` → `FingerprintRadarProjection.Project(fp, archetype, layout, effectiveWeights)`. Same projection used by `BotDetectionDetailsViewComponent.cs:102` — keep parity.
- **Extract a shared partial:** lift the 7-axis SVG-rendering block from `Views/Shared/Components/BotDetectionDetails/Default.cshtml:34-110` into `Views/StyloBot/Dashboard/_FingerprintShape.cshtml` (model: `FingerprintRadarShape` + `string accent` + `bool isBot` + `string verdictLabel`). Server-side render, no JS fetch.
- `BotDetectionDetails/Default.cshtml` switches to `@await Html.PartialAsync(...)` of the new partial — home card untouched visually.
- `_SignatureDetail.cshtml:179-197` swaps the `_BehavioralEvolution` invocation for the same partial: `@await Html.PartialAsync("~/Views/StyloBot/Dashboard/_FingerprintShape.cshtml", new _FingerprintShapeModel { Shape = Model.FingerprintShape, ... })`.
- **Delete the abandoned scaffold** (no longer reachable after the swap):
  - `Views/StyloBot/Dashboard/_BehavioralEvolution.cshtml`
  - `Models/BehavioralEvolutionModel.cs`
  - `Configuration/BehavioralEvolutionOptions.cs`
  - `StyloBotDashboardOptions.BehavioralEvolution` property + its binding
  - The `/api/sessions/signature/{sig}` middleware route if no other consumer remains (audit with grep before deletion).

**Note:** session-overlay (the "Behavioral Evolution" snapshot ghosts) was Phase D earlier — explicitly deferred. The new partial renders the current shape only. When Phase D ships, it'll add the overlay as a sibling polygon series passed into the same partial.

### Section G — Header-name consistency

**Goal:** one prefix, one canonical set.

**Decision:** use `X-Bot-Detection-` (matches what `DetectionDataExtractor.TryExtractFromYarpHeaders` already reads). Migrate the two `X-StyloBot-*` response writes in `BotDetectionMiddleware.cs:394,398` to `X-Bot-Detection-VerdictSource` and `X-Bot-Detection-IdentityFingerprint`. Update `docs/fingerprint-verdict-cache.md`.

### Section H — Options: `EmitOnResponseToClient` + `HeaderPrefix`

**Goal:** every header behaviour configurable per the all-settings-configurable rule.

**Change in `EdgeForwardedHeadersOptions`:**
- `EmitOnResponseToClient` (default `false`) — when true, the same identity headers also go on the response back to the client (JS-tool opt-in).
- `HeaderPrefix` (default `"X-Bot-Detection-"`) — propagates to `StyloBotEdgeHeaderNames`. The `All` static field becomes a runtime list computed from the prefix.

### Section I — Ctor inject + drop redundancies

- `StyloBotForwardedHeadersMiddleware`: ctor-inject `IFingerprintReader?` (nullable, for hosts without it). Drop `RequestServices.GetService<>`.
- `StyloBotInboundClientHeaderStripperMiddleware`: drop `ContainsKey` guard. `Remove` is idempotent.
- Extract `TryGetEvidence(HttpContext) → AggregatedEvidence?` helper. Use it.

### Section J — Compose API key via env var

**Change in `docker-compose.test.yml`:** replace inline `staging-test-website-key-do-not-use-elsewhere` (two sites) with `${WEBSITE_GATEWAY_API_KEY:?Set WEBSITE_GATEWAY_API_KEY in .env.test}`. Document in `.env.test.example`.

## Sequencing

1. **Section A** (cache layer) lands first. It's the foundation — every section after it benefits from no per-request DB calls.
2. **Section B + C + D** (priority race + skip path + Items↔Signals mirror) — together, because the skip-path fix depends on the mirror, and the mirror lets Section B remove the synthetic-broadcast.
3. **Section E** (hydrator).
4. **Section F** (visitor-detail radar).
5. **Sections G, H, I, J** (cleanup) — independent, can land as one commit.

Each section: one commit. Build clean + tests pass locally before push.

## Exit criteria

- Home card on `/` renders the fingerprint radar.
- Signature detail page `/dashboard/signature/{id}` renders the fingerprint radar.
- No raw `SqliteConnection` instantiation in any middleware or view component.
- `FingerprintMatcherConvergenceTests` 5/5 green.
- New `CachedFingerprintReaderTests` green.
- Gateway log per-request output: clean (no per-request hydrator INFO spam).
- Playwright confirms both pages rendering, screenshots captured.
