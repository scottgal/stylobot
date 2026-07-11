# Composite browser-mode fingerprints

## Concept

One identity, many shapes. A fingerprint stays a single identity bound to one `primary_signature` lookup, but its centroid is no longer monolithic. The fingerprint now holds a small set of **mode centroids** — one per distinct request shape the same browser plays during a session. A real Chrome user emits a *navigation* mode on the initial page load, an *xhr* mode on subsequent API fetches, a *sub-resource* mode on stylesheet/image pulls, and a *signalr-negotiate* mode on hub connects. All four are the same identity, all four are the same fingerprint row, but each mode has its own centroid, its own weights, its own observation count, its own nearest archetype.

This collapses a long-standing tension. Today's archetype scoring forces a single fingerprint to drift between `chrome-desktop` and `chrome-xhr` as the centroid absorbs requests of different shapes. The drift is cosmetic — same person, same UA, same IP — but the matcher reads it as evolution. With modes, the navigation observations roll into the navigation centroid, the xhr observations roll into the xhr centroid, neither pollutes the other, and the *fingerprint* stays steady.

It also opens an access-control surface that doesn't exist today: per-endpoint policies that key on which mode is calling. "Block xhr-mode requests to `/admin` but allow navigation-mode" closes a real XSS-fetch exfiltration path. "Require a navigation mode to have appeared in the same fingerprint's last N observations before allowing `/api/secrets`" makes bare API hits without a real page load fail closed.

## Why this is not a new primitive

Browser modes are *related centroids*. The existing fingerprint pipeline already understands centroids, EWMA absorption, per-fingerprint weight vectors, drift detection, archetype matching, brute-force cosine search. None of that changes. The mode row reuses the existing fingerprint storage schema verbatim — same blob layout, same maturity math, same observation absorption. The only addition is a child table that holds N centroids per fingerprint instead of one, plus a tiny request classifier that decides which mode a given request belongs to.

The matcher's two-pass design also stays. Pass 1 still resolves `primary_signature → fingerprint_id`. The new step is *within* the fingerprint: pick the mode row whose centroid the request vector is closest to. Pass 2 (brute-force cosine across nearby fingerprints) now iterates over mode centroids instead of fingerprint-monolithic centroids — same scan, same cost, finer granularity.

## What a browser mode is

A mode is a request-shape class, decided per request from already-encoded raw values. The entire inventory lives in YAML (`Definitions/BrowserModes/*.yaml`). There is no C# enum of mode ids, no hand-written switch on mode names, no allowlist of Accept strings or SignalR paths anywhere in code. The classifier loads the YAML once, compiles each predicate into the existing signal-pattern primitive, and walks them in document order returning the first match. New modes land as new YAML files, no code change. New SignalR/WebSocket path families land as new YAML predicates, no code change. The summary below describes the initial YAML inventory and is documentation only — the table is generated from the YAML, not enforced by it.

| Mode           | YAML predicate summary                                                                             |
| ------------------- | -------------------------------------------------------------------------------------------------- |
| `navigation`        | `sec_fetch_pattern ∈ {7,15}` AND `method=GET` AND `accept` contains `text/html`                    |
| `xhr`               | `sec_fetch_pattern=7` AND `accept` matches the XHR/fetch family declared in YAML                   |
| `sub-resource`      | `sec_fetch_pattern=7` AND `method=GET` AND `accept` matches the sub-resource family declared in YAML |
| `signalr-negotiate` | `sec_fetch_pattern=7` AND `method=POST` AND path matches the SignalR hub family declared in YAML   |
| `websocket-upgrade` | `Upgrade: websocket` present                                                                       |
| `prefetch`          | `Sec-Purpose: prefetch` present                                                                    |
| `bot-raw`           | `sec_fetch_pattern=0` (no Sec-Fetch-* headers at all — googlebot, curl, python-requests)           |
| `unknown`           | Fallback when no mode predicate matches (configurable id, default `unknown`)                  |

The volatile slots that overspecified browser archetypes (`sec_fetch_pattern`, `upgrade_insecure_requests`) move *into* the mode predicate where they belong, and *out of* the archetype assertions where they didn't. Per [[feedback_no_word_lists]]: no hand-rolled curated string switches when YAML/loader/config already owns that data.

## Storage

```sql
CREATE TABLE fingerprint_modes (
    fingerprint_
        id        TEXT NOT NULL REFERENCES fingerprints(fingerprint_id) ON DELETE CASCADE,
    mode_id          TEXT NOT NULL,
    centroid              BLOB NOT NULL,
    centroid_maturity     INTEGER NOT NULL,
    weights               BLOB NOT NULL,
    observation_count     INTEGER NOT NULL,
    first_seen            TEXT NOT NULL,
    last_seen             TEXT NOT NULL,
    inferred_archetype    TEXT,
    inferred_confidence   REAL,
    PRIMARY KEY (fingerprint_id, mode_id)
);

CREATE INDEX ix_fingerprint_modes_last_seen
  ON fingerprint_modes(last_seen);
```

le The parent `fingerprints` row keeps its existing `centroid` column. That column now holds the **rollup centroid** — a weighted mean of the mode centroids, recomputed on a schedule-coordinator tick. The rollup is what Pass 2's index search reads when looking for nearest fingerprints, so the existing index path stays compatible.

Migration is observation-driven. Every existing fingerprint gets seeded with one synthetic mode row keyed `unknown` (the id is configurable), holding the row's current centroid/weights/maturity verbatim. As the fingerprint's subsequent observations arrive, the mode classifier splits them off into real mode rows. The `unknown` row decays as its observation count stops growing. The parent `centroid` column is recomputed from children on each rollup tick — within a few observations per fingerprint, the rollup stabilises.

The Postgres and SQLite stores both gain the `fingerprint_modes` table on the same idempotent schema run. There is no parallel in-memory mode store — per [[feedback_no_inmemory_stores]], `ConcurrentDictionary` is never the persistence default. The mode read path is an LFU-bounded write-through façade over the DB row, served by a `BrowserModeHotCacheAtom` whose lifetime is tight to the existing fingerprint hot cache atom. The atom holds the warm mode tuples (parent fingerprint + N modes) keyed by `fingerprint_id`, evicts by LFU when bounded, and on miss reads through to the store the same way `SqliteFingerprintStore` reads through today. The reference shape is `SqlitePathLifecycleStore` (`src/Mostlylucid.BotDetection/Lifecycle/SqlitePathLifecycleStore.cs`) and the universal pattern in [[feedback_write_behind_lfu_facade]]: dict is truth on the hot path, DB is durability.

Writes are signal-driven, not Task-driven. Per [[feedback_signals_atoms_pattern]] and [[feedback_no_background_services]]: when the matcher absorbs an observation, it emits a `fingerprint.mode.absorbed` signal on the `SignalSink` carrying `(fingerprint_id, mode_id, new_centroid, new_weights, new_maturity, last_seen)`. A `BrowserModePersistenceAtom` subscribes to that signal pattern; its `OnAbsorbedAsync` updates the cache atom's dict (which IS the truth) and accumulates the row delta. The accumulated deltas drain to the DB when the `tick.10s` cadence signal fires on the `ScheduleCoordinator`, in one batched UPSERT per store per tick. The drain is just another signal handler — there is no `Channel<T>`, no `Task.Run`, no `BackgroundService`. The Ephemeral runtime owns concurrency through the signal pipeline; the persistence atom is a participant.

Backpressure is the same shape as the rest of the system: under load, the schedule coordinator coalesces ticks; under sustained pressure, the slow-path coordinator gates write-bound work; the atom never accumulates unbounded state because it is bounded by the LFU cap on the cache atom it serves. Eviction from the cache atom emits a `fingerprint.mode.evicted` signal which the persistence atom uses to flush that fingerprint's pending deltas before the row leaves memory — no dropped writes.

## Per-request flow

1. **IdentityVectorAtom composes the per-request vector** — unchanged. The same raw-values dict that today produces a layout-conformant float vector.
2. **BrowserModeClassifierAtom** — new atom subscribing to the existing per-request signal pattern. Reads the raw-values from the `SignalSink`, walks the YAML predicates compiled into `SignalPatternMatcher`s, emits `identity.browser_mode` (the mode id) on the same sink. Cost: microseconds. No state, no I/O.
3. **L1 lookup** — `primary_signature → fingerprint_id`, unchanged. Same `HMAC(IP, UA)` key, same LFU-cached lookup served by the existing fingerprint hot cache atom.
4. **Load fingerprint + its modes** — the `BrowserModeHotCacheAtom` serves the tuple from its dict; on miss, it reads through to the store (single join query on `fingerprints` + `fingerprint_modes`) and populates the dict.
5. **Pick the mode row** matching the emitted `identity.browser_mode`. If absent: allocate a new mode row, seed from the request vector (no archetype prior — the parent fingerprint is already confirmed identity, the mode is just learning its shape). The allocation is in-dict immediately; persistence happens via the absorbed-signal handler on the next `tick.10s`.
6. **L1 confirm** — weighted cosine of the request vector against *the mode's* centroid using the mode's own weights composed with global weights. Same code path as today's L1 confirm; the inputs are the mode row instead of the fingerprint row.
7. **On confirm**: EWMA-absorb the request vector into the mode's centroid, update its weights via stability learning, increment its `observation_count`. The matcher emits `fingerprint.mode.absorbed` on the `SignalSink`; the `BrowserModePersistenceAtom` listens and accumulates the row delta for the next drain tick.
8. **On miss**: fall through to Pass 2. Pass 2's brute-force scan iterates over mode centroids of nearby fingerprints (the existing index keys remain at the fingerprint level, but the candidate set unpacks to modes). The matched mode's parent fingerprint is the resolved identity.
9. **Rollup recompute** — a `FingerprintRollupRecomputeAtom` subscribes to `tick.5m`. On each fire it walks the LFU-warm fingerprints in the cache atom, recomputes parent `centroid` as a weighted mean of child modes, emits a `fingerprint.rollup.updated` signal which the persistence atom drains alongside the mode deltas on the next `tick.10s`. No new `BackgroundService`, no `Task.Run`, no timer loop.

## Signals

In addition to today's `identity.fingerprint_id`, `identity.client_type`, `identity.match_score`:

| Signal                              | Type           | Meaning                                                                                |
| ----------------------------------- | -------------- | -------------------------------------------------------------------------------------- |
| `identity.browser_mode`                | string         | The mode this request was classified as                                           |
| `identity.browser_mode_age_seconds`    | double         | Wall-clock age of this mode for this fingerprint                                  |
| `identity.browser_mode_archetype`      | string         | The nearest archetype to this mode's centroid (e.g. `chrome-xhr`)                 |
| `identity.browser_mode_score`          | double         | Cosine score between the request vector and the mode's centroid                   |
| `identity.browser_mode_unseen`         | bool           | True when this is the first time this fingerprint has shown this mode             |
| `identity.browser_mode_mix`            | dict<str,int>  | Per-fingerprint observation counts per mode (e.g. `{nav:5, xhr:28, sub-res:12}`) |
| `identity.browser_mode_count`          | int            | Number of distinct modes this fingerprint has played                              |

`identity.client_type` keeps emitting today's value (the archetype matched against the *rollup* centroid) so every existing consumer keeps working unchanged. The mode-flavoured signals are additive.

The blackboard atom that holds identity already owns these fields under the existing signal pattern — modes extend that atom, they do not introduce a parallel state. The signal naming follows the existing `identity.*` namespace.

## Endpoint policies — mode predicate

`BotDetection:EndpointPolicies` gains an optional `mode` clause. The existing `IEndpointPolicyResolver` reads the `identity.browser_mode` signal from the blackboard at policy evaluation time and matches against the clause.

```yaml
endpoints:
  - path: /admin/**
    mode_in: [navigation]
    deny_message: "Direct API access to admin is not allowed"

  - path: /api/**
    mode_in: [xhr, signalr-negotiate]

  - path: /api/secrets
    mode_required: navigation_within_session
    # this fingerprint must have shown the navigation mode in the
    # last N observations (N from BrowserModeOptions.NavigationWithinSessionWindow)
    # before any xhr-mode request to this path is allowed.

  - path: /hub/negotiate
    mode_in: [signalr-negotiate]
```

`mode_in` is a hard predicate (membership). `mode_required: <predicate>` is a small set of named predicates whose evaluation lives in the resolver (`navigation_within_session`, `any_browser_mode`, etc.). The named predicates are defined alongside the mode YAML so the inventory stays in one place.

This composes cleanly with the existing per-endpoint rate-limit, geo, and group-permission clauses. The mode clause is just one more axis the resolver consults.

## Browser modes as an automation detector

Browser modes are not just a unification mechanism. The same per-mode statistics that hold the identity together expose automation that mimics a browser at the UA + header level but cannot mimic the *shape* of a real browsing session. Five independent axes drop out of the same observation pipeline:

1. **Mode mix.** A real human session shows a characteristic ratio: typically `1 navigation : 8–20 xhr : 3–5 sub-resource : ≤1 signalr-negotiate`. A scripted client shows a degenerate ratio — pure `xhr`, pure `bot-raw`, or `navigation` only with no follow-up sub-resources. The per-site human-norm histogram is the existing archetype-baseline reuse from [[project_human_norm_baseline]]; the distance from it is the score.
2. **Per-mode byte transmission.** Each mode has a typical request/response size band — `navigation` carries a real Accept-Language + cookies + Referer payload; `xhr` carries JSON of bounded size; `sub-resource` requests are short, responses long; `signalr-negotiate` is small both ways. Scripted clients flatten these — every `xhr` looks identical in size, or every `navigation` carries no cookies. Per-mode byte stats land on the same fingerprint row, computed from observations the pipeline already records.
3. **Per-mode inter-arrival timing.** Real browsers issue `navigation` once, then a burst of `sub-resource` within the first few hundred ms, then sparse `xhr` driven by user interaction. Scripted clients show metronomic intervals (CRON-shape) or zero-spread bursts (`xhr` flood with no nav). Inter-arrival statistics on the mode timeline are a per-fingerprint atom that updates on every observation.
4. **First-appearance order.** A real browser plays `navigation` first, then `sub-resource`, then `xhr` (driven by JS that the navigation loaded). A scripted client jumps straight into `xhr` against an authenticated path. "First mode ever observed on this fingerprint = `xhr`" is a strong signal on its own.
5. **Unseen-mode emergence.** A fingerprint that has played `navigation` + `xhr` + `sub-resource` for a week and suddenly shows `bot-raw` is either a takeover or a tool running under the same identity context. `identity.browser_mode_unseen=true` is a per-request anomaly flag.

All five feed the unified `SignatureRiskVerdict` ([[project_signature_risk_verdict]]) as a single `BrowserModeMixDeviation` axis whose components are weighted in YAML, not code. The composer maps it onto the `RiskProfile` operator axis. Bots that get past UA + header detection because their pattern library is current still cannot synthesise a plausible mode timeline without genuinely driving a browser — and a real browser, once driven, costs at script-time what a real user costs at session-time, which collapses scraper economics.

Cross-mode drift (a fingerprint started showing a mode class it had never shown before) is the per-request manifestation of axis 5 and feeds into the same composer through the `identity.browser_mode_unseen` signal.

### Baseline is learned per site, not seeded

The five axes above all measure deviation *from a baseline*. The baseline is not a global prior in YAML. Every site's human traffic mix is different — a docs site is mostly `navigation` + `sub-resource`; a SPA dashboard is mostly `xhr` + `signalr-negotiate`; a static blog is almost pure `navigation`. A YAML prior that called "mostly xhr" suspicious would label every legitimate SPA user a bot on day one.

The system starts neutral. The mode-mix-deviation axis returns 0 (no contribution to the verdict) until the site has accumulated enough mature, classification-confirmed fingerprints to bootstrap a baseline. Two baselines fold in independently as the data arrives:

- **Human baseline** — fed by fingerprints whose `Verdict.IsHumanFriendly` latch is set (today's archetype-friendly + verified-human signal path) AND whose per-mode centroids have crossed `MinModeMaturityForBaseline`. Each qualifying fingerprint contributes its mode mix, byte-per-mode stats, inter-arrival timings, and first-appearance order into a per-site human EWMA.
- **Bot baseline** — fed by fingerprints whose `Verdict.IsConfirmedBot` latch is set (today's verified-bot + ConfirmedBad fastpath) AND whose per-mode centroids have crossed the same maturity threshold. Same per-site EWMA structure, separate row.

A `BaselineMaturityAtom` subscribes to the same `fingerprint.mode.absorbed` signal the persistence atom listens to; when an observation pushes a mode's centroid maturity over the threshold for the first time and the fingerprint carries a verdict latch, the atom emits `site.baseline.contribution` carrying the qualifying stats. A `SiteBrowserModeBaselineAtom` subscribes to that signal, EWMA-folds the contribution into its per-site row, and emits `site.baseline.updated` so cache invalidation tracks the change through the central [[feedback_centralised_change_detection]] mechanism. There is no `Task.Run`, no timer, no separate calibration service — the maturity events trigger the fold-in naturally as they happen.

A site that has not yet accumulated `MinHumanFingerprintsForBaseline` mature human contributors AND `MinBotFingerprintsForBaseline` mature bot contributors has `BrowserModeMixDeviation = 0` for every signature — the axis is honestly unhelpful at that stage, and the verdict composer reflects that rather than guessing. Once both baselines clear maturity, the axis turns on and contributes per the YAML-weighted component model. Operators see a `Baseline: learning (12/50 humans, 3/20 bots)` indicator on the dashboard while the site is still bootstrapping, which surfaces the state honestly rather than silently scoring zero.

The same maturity discipline applies to the *bot* baseline. A small number of confirmed bots is enough to anchor the floor of the distribution (extreme deviations score regardless of bot-baseline size); the bot baseline mostly tightens the mid-range. Per [[project_centroid_learning_feedback_loop]], every contribution also carries `action_policy_id + enforcement_mode + enforcement_outcome + traffic_class + policy_revision` so the baseline can later be segmented into "natural prior" vs "post-enforcement shape" without re-derivation.

Per-site baselines, like every other write-through atom in the system, are LFU-bounded and persist through the existing signal-sink + persistence-atom pattern; no parallel in-memory store, no TPL.

## Archetype YAML — no change

`chrome-desktop`, `chrome-xhr`, `firefox-desktop`, `mastodon`, `googlebot` and the rest stay as they are. They are global priors. With modes, the matcher uses them naturally: each mode row matches against the archetypes whose YAML predicates overlap with the mode's natural slot population. The navigation mode of a Chrome fingerprint matches `chrome-desktop`; the xhr mode of the same fingerprint matches `chrome-xhr`; the *fingerprint* shows both, no drift, no split.

The masked-similarity fix that landed in `032821b9` continues to do its job — it's the per-mode archetype scorer. Empty/null observation slots still produce zero scores. Tightening `firefox-desktop` to drop navigation-volatile assertions also stays correct, but under modes the same model can be applied uniformly: archetypes that exist today as `*-desktop` describe the *navigation* shape, archetypes that exist as `*-xhr` describe the *xhr* shape. Both stay.

`Definitions/IdentityArchetypes/*.yaml` may gain an optional `applies_to_mode` field that hints which mode class the archetype is designed for. The matcher uses it to bias archetype selection per mode. Backward-compatible: omit the field and the archetype is eligible for any mode (today's behaviour).

## Dashboard surface

The signature detail page gains a **Modes** panel below the existing fingerprint profile section. One row per mode with:

- mode id (`navigation`, `xhr`, ...)
- observation count
- first-seen / last-seen
- nearest archetype (`chrome-desktop` / `chrome-xhr`)
- per-mode drift sparkline (uses the existing drift surface, scoped to the mode centroid)
- "click to filter visitors timeline by this mode"

The "Drifted Chrome Desktop → Chrome XHR DRIFT VS 61.4%" misnomer today's signature page shows is replaced by an honest "this signature has played chrome-desktop (navigation) and chrome-xhr (xhr) — both consistent with one Chrome user." Drift, when it really happens, is per-mode — chrome-desktop centroid migrating toward headless-chrome over time is a real signal worth surfacing; navigation vs xhr on the same fingerprint is not.

The Visitors list gains a mode filter so the operator can pivot "show me all signatures that have ever played `signalr-negotiate`" or "show me signatures that ONLY play `xhr` (no nav)". The latter is the bot-impersonating-browser query.

The Investigation view (`project_investigation_view`) gains mode as a facet — the radar visualisation can render each mode independently or the rollup centroid, switchable.

Per the dashboard cache-invalidation rule (`feedback_centralised_change_detection`), the new `Modes` widget reads through the same central change-detection mechanism. No private warmup, no private TTL.

## SDK / remote-mode

Gateways already serve their LFU-hot fingerprints over the SDK REST surface. Modes extend the existing fingerprint DTO with an optional `modes: Mode[]` field. SDK clients that ignore the field get today's behaviour. The remote-mode optional-DI rule (`feedback_remote_mode_optional_di`) applies: dashboard read paths that consume modes early-return-on-missing-mode-list rather than throwing.

## Configurable settings

```csharp
public sealed class BrowserModeOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxModesPerFingerprint { get; set; } = 8;
    public double MinModeMaturityForArchetypeMatch { get; set; } = 3.0;
    public double MinModeMaturityForBaseline { get; set; } = 8.0;
    public int MinHumanFingerprintsForBaseline { get; set; } = 50;
    public int MinBotFingerprintsForBaseline { get; set; } = 20;
    public TimeSpan UnseenModePruneAge { get; set; } = TimeSpan.FromDays(7);
    public int UnseenModePruneMinObservations { get; set; } = 3;
    public string FallbackModeId { get; set; } = "unknown";
    public int NavigationWithinSessionWindow { get; set; } = 50;
}

public sealed class SiteBrowserModeBaselineOptions
{
    public double HumanEwmaAlpha { get; set; } = 0.05;
    public double BotEwmaAlpha { get; set; } = 0.10;
    public double DeviationAxisWeightInRiskProfile { get; set; } = 0.25;
    public TimeSpan BaselineStaleAfter { get; set; } = TimeSpan.FromDays(30);
}
```

All thresholds, intervals, and caps live on the Options class per the `feedback_all_settings_configurable` rule.

## Schedule coordinator hookups

No new `BackgroundService`, no `Task.Run`, no `Channel<T>`, no timer loop. Per [[feedback_no_background_services]] and [[feedback_signals_atoms_pattern]]: browser modes are participants on the existing tick signal pattern, nothing more.

- `BrowserModePersistenceAtom` subscribes to `tick.10s` — drains accumulated absorb/rollup deltas in one batched UPSERT per store.
- `FingerprintRollupRecomputeAtom` subscribes to `tick.5m` — sweeps the LFU-warm cache atom, recomputes parent rollup centroids, emits `fingerprint.rollup.updated`.
- `BrowserModeRowPruneAtom` subscribes to `tick.1h` — drops mode rows where `observation_count < UnseenModePruneMinObservations` AND `last_seen < now - UnseenModePruneAge`. Cache atom gets the prune signal first so eviction-flush runs before the DB delete.
- `BrowserModeArchetypeReclassifyAtom` subscribes to `tick.1m` — re-runs archetype scoring for modes whose centroid maturity passed `MinModeMaturityForArchetypeMatch` since the last tick, emits updated `inferred_archetype`/`inferred_confidence`, gated by the existing slow-path coordinator under sustained pressure.

The schedule coordinator already handles cadence + back-pressure + coalescing. Each atom is tight to its parent sink and dies when the sink dies (same lifecycle as `SignatureEscalatorAtom`). Nothing here invents a new threading primitive.

## Test surface

BDF replay scenarios extend to assert per-mode outcomes. The existing `BdfReplayTests.HumanScenario_DoesNotMisclassify_AndPipelineFeedsDownstreamSignals` theory keeps its current asserts and adds a new one: every scenario's request sequence must produce the expected mode mix, and the underlying fingerprint id must be stable across mode changes. A new bug filter: any human BDF scenario that produces a *new fingerprint* per mode is a regression of this work.

New unit tests:

- `BrowserModeClassifierTests` — YAML loading, decision walk, fallback behaviour.
- `FingerprintBrowserModeAbsorptionTests` — per-mode EWMA, weight stability learning, drift detection scoped to a mode.
- `FingerprintRollupRecomputeTests` — parent `centroid` after rollup matches the weighted mean of children.
- `EndpointPolicyBrowserModePredicateTests` — `mode_in` and `mode_required` match the request's `identity.browser_mode` signal correctly, both allow and deny paths.
- `BrowserModeMixDeviationTests` — the per-fingerprint mode histogram feeds the verdict composer with the right `RiskProfile` contribution.

## Build sequence

1. **Mode classifier + YAML inventory** — `Definitions/BrowserModes/*.yaml`, `BrowserModeRegistry`, signal emission. No persistence change. Wire into the orchestrator after `IdentityVectorAtom`. End state: every request emits `identity.browser_mode`, dashboard sees it, no downstream consumer breaks.
2. **`fingerprint_modes` schema + LFU façade** — both stores. The migration project emits the idempotent SQL. Existing fingerprints seeded with one `unknown` mode row. End state: schema landed, no behavioural change.
3. **Per-mode matcher** — `FingerprintMatchAtom` learns to read modes, do L1 confirm against the matched mode, EWMA-absorb into the mode's centroid. End state: same identity stability as today, with the new mode signals fully populated.
4. **Rollup recompute on `tick.5m`** — recompute parent `centroid`, drift signals against rollup. End state: Pass 2 + index search keep working unchanged.
5. **Endpoint policy mode predicate** — `EndpointPolicyResolver` consumes `identity.browser_mode`, `mode_in` / `mode_required` clauses go live. New configurable settings on `BotDetection:EndpointPolicies`. End state: per-mode access control available.
6. **Mode mix anomaly axis** — `SignatureRiskVerdictComposer` learns `BrowserModeMixDeviation`. Dashboard `Risk Profile` axis surfaces it. End state: bot-impersonating-browser detection sharpens.
7. **Dashboard Modes panel + Visitors filter** — UI work, reuses existing SSR-then-SignalR-beacon pattern (`feedback_ssr_signalr_pattern`). End state: operator can see, filter, and drill in by mode.
8. **SDK extension + remote-mode optional plumbing** — additive DTO field, SDK clients that don't read it stay compatible. End state: parity between gateway-local and remote-mode dashboards.
9. **Per-site baseline learning** — `BaselineMaturityAtom` + `SiteBrowserModeBaselineAtom` wire up against `fingerprint.mode.absorbed`, fold mature classification-confirmed contributions into per-site human/bot EWMA rows, dashboard surfaces `Baseline: learning (N/M humans, P/Q bots)` until both clear maturity. End state: the deviation axis turns on per-site as soon as the data justifies it, never before.

Each step ends green: BDF replays pass, no regressions, dashboard renders. The order keeps every step independently shippable. Steps 1–4 give us unified identity (no more chrome-desktop → chrome-xhr drift); pausing there is fine. Step 5 opens the endpoint policy surface. Steps 6 + 9 are the automation-detector substrate — they ship live but contribute zero to the verdict until each site's baseline matures, so they are safe to land before any operator-visible scoring change.

## What this preserves verbatim

- `primary_signature` scheme.
- L1 cache (LFU write-behind façade).
- Pass 2 brute-force search.
- Weighted cosine math (`BruteForceIdentityAnchorIndex.WeightedCosine`).
- Masked-similarity for archetype scoring (`032821b9`).
- License gating layer.
- Dashboard ↔ gateway signal contract for everything that doesn't touch modes.
- All existing archetype YAML.

## What this fixes that isn't on today's list

The "Risk Profile = behavioural deviation from human-norm baseline" axis (`project_risk_profile_semantics`) finally has a clean substrate. Today the baseline is "the fingerprint's centroid vs the archetype centroid" — a monolithic comparison that loses information whenever a real browser plays multiple shapes. With modes, the baseline becomes "this fingerprint's mode mix vs the per-site human-norm mode mix" — strictly more signal, strictly less noise. The fingerprint-anchored OTel + logs idea (`project_fingerprint_anchored_otel_logs`) can also tag spans with the mode that produced them, so the per-fingerprint sliding window naturally shows the mode timeline.