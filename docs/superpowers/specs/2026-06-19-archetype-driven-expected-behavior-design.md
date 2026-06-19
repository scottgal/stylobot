# Archetype-Driven Expected Behavior Design

> Status: design (brainstormed 2026-06-19 with user). Companion immediate-fix
> committed as `fix(risk): clamp RiskBand to Low for Internal (network-trusted)`
> at `a2913eed`. This spec is the durable architecture that supersedes the clamp.

## Problem

The dashboard surfaces a contradictory pairing for trusted bot types — most
visibly **StyloBot Internal · Policy: Allow · Probability 100% · Risk Profile
VeryHigh**. The four operator-facing axes (Probability, Confidence, Risk
Profile, ThreatBand) are supposed to be independent and coherent; today they
slide into incoherence whenever a high-probability identity is *expected* to
be high-probability. The underlying gap: the system has no concept of
**expected behavior** for an identity. It knows what an identity looks like
(header SHAPE in archetypes; UA-string match via Arcjet); it does not know
what the identity is supposed to DO. Without that, every named-and-trusted
bot looks alarming under sustained load.

The same gap shows up downstream:

- **Googlebot at 5rps on `/sitemap.xml`** and **GPTBot at 200rps on `/admin/*`**
  land identically as "100% bot probability"; the dashboard cannot
  distinguish them by behavior shape.
- The "is this still the same fingerprint?" question (Multi-Factor Signatures)
  has no per-archetype guidance — every dimension is treated as equally
  identity-defining, which causes premature splits or sticky stale identities.
- Operators can't pin per-signature constraints like "this is the nightly
  cron — expect 60rps between 02:00–03:00 UTC only".

## Design idea (one sentence)

**Extend the archetype YAML schema so any BDF dimension can carry an
expected value + tolerance shape + drift role; reuse the existing centroid /
drift / Multi-Factor Signature machinery to surface alignment, behavior
deviation, and identity-continuity in one record per request.**

No parallel data path, no new service abstraction. Arcjet stays the identity
catalog; our extended archetypes ride on top via BotType inheritance.

## Architecture

```
┌─ Identity (existing) ────────────────────────────────────────────────┐
│  Arcjet well-known-bots catalog (600+)                               │
│      .id  .categories  .pattern.accepted/.forbidden  .url            │
│             │                                                        │
│             └─ MapBotType ─► BotType enum                            │
│                                  │                                   │
└─────────────────────────────────  │  ──────────────────────────────  ┘
                                    │
┌─ Expected behavior (NEW) ──────── │  ──────────────────────────────  ┐
│                                   ▼                                  │
│  Tier 1 — bot-type default archetype                                 │
│      Definitions/IdentityArchetypes/_bot_type/<botType>.yaml         │
│      One YAML per BotType enum value (7 total).                      │
│                                                                      │
│  Tier 2 — specific-bot override (opt-in)                             │
│      Definitions/IdentityArchetypes/<archetype-id>.yaml              │
│      Only created when vendor publishes specifics.                   │
│                                                                      │
│  Tier 3 — per-signature pin (runtime)                                │
│      signature_pins table (sqlite + postgres), dashboard-edited.     │
│                                                                      │
│  Compose order: Tier 3 ▸ Tier 2 ▸ Tier 1. Lower tier overrides       │
│  upper at the dimension level. Missing dimension at a tier: inherit. │
└──────────────────────────────────────────────────────────────────────┘
                                    │
┌─ Evaluator (NEW) ──────────────── │  ──────────────────────────────  ┐
│                                   ▼                                  │
│  ArchetypeAlignmentEvaluator (sealed, stateless)                     │
│      Inputs: BlackboardState + composed archetype (Tier 1+2+3).      │
│      Outputs (per request):                                          │
│        • DimensionVerdict[]  — per-slot expected/observed/distance   │
│        • bool IdentityHolds  — all drift_role=identity dims aligned  │
│        • bool BehaviorAligned — all drift_role=behavior in tolerance │
│        • double WeakDeviation — scaled sum of drift_role=weak        │
│        • string? BreakAction — "split" when identity_continuity      │
│                                rule violated over time_window        │
│      Writes signals to blackboard for existing consumers:            │
│        archetype.alignment.identity_holds                            │
│        archetype.alignment.behavior_aligned                          │
│        archetype.alignment.weak_deviation                            │
│        archetype.alignment.break_action                              │
│        archetype.alignment.deviated_slots                            │
└──────────────────────────────────────────────────────────────────────┘
                                    │
┌─ Downstream consumers (REUSED) ── │  ──────────────────────────────  ┐
│                                   ▼                                  │
│  SignatureRiskVerdictComposer                                        │
│      New input: ArchetypeAlignmentResult.                            │
│      New gating rule (trusted-and-aligned clamp), inserted between   │
│      friendly-pin and the raw-score bucket:                          │
│        if PrimaryBotType ∈ {Internal, VerifiedGoodBot}               │
│           and IdentityHolds and BehaviorAligned:                     │
│           RiskBand = VeryLow                                         │
│      (Supersedes the immediate-fix clamp at a2913eed.)               │
│                                                                      │
│  MultiFactorSignatures                                               │
│      Consumes archetype.alignment.break_action="split" on the next   │
│      match attempt: treats the new shape as a candidate distinct     │
│      fingerprint instead of partial-match-rescuing it. No change to  │
│      MFS internals — just one new input.                             │
│                                                                      │
│  Signature detail UI                                                 │
│      New "Expected behavior" panel (see UI section).                 │
└──────────────────────────────────────────────────────────────────────┘
```

## YAML schema

Extending the existing archetype YAML. Each dimension can keep the current
shorthand (`value:` + `confidence:`) **or** use the long form below. Existing
archetypes load unchanged; the loader treats short form as
`tolerance: exact`, `drift_role: behavior`, `weight: 1.0`.

```yaml
archetype_id: stylobot-internal
name: StyloBot Internal Client
archetype_kind: tool                  # existing
inherits_from: _bot_type/internal     # NEW — Tier 1 lookup key (optional)

dimensions:
  hdr.ua_family:
    expected: "StyloBot.Internal"
    tolerance: exact
    drift_role: identity              # break ⇒ new fingerprint candidate
    weight: 1.0

  session.request_count:
    expected: 60                      # rps target (sustained)
    tolerance:
      shape: range
      lower: 5
      upper: 500
    drift_role: behavior              # drift ⇒ RiskBand lift
    weight: 0.8

  session.frequency_periodicity_score:
    expected: 0.95                    # very periodic (cron-like)
    tolerance:
      shape: numeric_delta
      delta: 0.1
    drift_role: behavior

  network.country_code:
    expected: ["GB", "US"]
    tolerance: oneof
    drift_role: weak                  # drift ⇒ confidence drag only

# NEW: identity-continuity rule, fed to MultiFactorSignatures
identity_continuity:
  required: [hdr.ua_family, transport.tls_ja4]
  time_window: 24h
  break_action: split
```

### Tolerance shapes (4)

| Shape | Holds when | Example BDF slot |
|---|---|---|
| `exact` | Observed equals expected (categorical equality, case-insensitive for strings) | `hdr.ua_family`, `transport.tls_ja4`, `hdr.accept_encoding_ordered` |
| `range` | `lower <= observed <= upper` (numeric / scalar) | `session.request_count`, `session.velocity_magnitude` |
| `oneof` | Observed ∈ expected list | `network.country_code`, `hdr.sec_fetch_pattern` (enum-shaped) |
| `numeric_delta` | `|observed − expected| <= delta` | `session.frequency_periodicity_score`, `quality.transport_quality` |

Defaults for each shape's tolerance live on `ArchetypeAlignmentOptions` so
operators can globally tune sensitivity without re-editing every YAML
(per the `feedback_all_settings_configurable` rule).

### Drift roles (3)

| Role | Meaning when alignment FAILS |
|---|---|
| `identity` | "This is no longer the same fingerprint." Feeds `MultiFactorSignatures` via `break_action` over the configured `time_window`. |
| `behavior` | "Still the same identity but acting outside its expected envelope." Lifts RiskBand and surfaces in the UI deviation panel. |
| `weak` | "Marginal signal, don't act on it alone." Contributes to confidence drag, no RiskBand impact. |

## Arcjet integration

Arcjet stays the **identity source**. We add the **behavior surface** indexed
BY the Arcjet `BotType` mapping (`WellKnownBotIndex.MapBotType`).

### Three-tier inheritance

```
Tier 1 — _bot_type/<botType>.yaml   (NEW, 7 files)
   one per BotType enum: search-engine, ai-bot, social-media, monitoring,
   tool, good-bot, internal. Covers 95% of bots that have no specific
   vendor docs.

Tier 2 — <archetype-id>.yaml         (existing dir, opt-in)
   created only when a vendor publishes behavior specifics (Googlebot
   crawl-delay docs, GPTBot rate disclosures). Inherits Tier 1 via
   `inherits_from: _bot_type/<botType>`; redeclares only the dimensions
   it overrides.

Tier 3 — signature_pins table        (NEW, runtime, DB-backed)
   per-signature override row written by the dashboard. Columns:
     primary_signature, dimension_slot, expected_json, tolerance_json,
     drift_role, weight, created_at, created_by, expires_at
   Composer reads via existing IFingerprintStore LFU. Cleared when
   expires_at passes.
```

### Seed values for the 7 bot-type defaults

All `drift_role: behavior` unless noted. Rate values are p95 sustained.

| Archetype | `session.request_count` (rps) | `session.frequency_periodicity_score` | `network.country_code` | identity dims |
|---|---|---|---|---|
| `search-engine` | 5 (range 0–20) | 0.7 (`±0.3`) | `oneof: [US, IE, NL]` | `hdr.ua_family`, vendor IP cidr |
| `ai-bot` | 10 (range 0–50) | 0.5 | any | `hdr.ua_family` |
| `social-media` | 2 (range 0–30) | 0.4 (event-driven) | any | `hdr.ua_family`, Sec-Fetch-Site |
| `monitoring` | 1 (range 0–10) | 0.95 (very periodic) | `oneof:` vendor regions | `hdr.ua_family` |
| `tool` | 0.5 (range 0–5) | 0 (one-shot) | any | `hdr.ua_family` |
| `good-bot` | 1 (range 0–10) | 0.5 | any | `hdr.ua_family` |
| `internal` | 60 (range 5–500) | 0.95 (cron-driven) | LAN cidrs | `hdr.ua_family = "StyloBot.Internal"` |

### Arcjet metadata surfaced live (no copy)

- Arcjet's `url` flows through to the UI panel as "Vendor docs: …" — read
  from `WellKnownBotEntry` by id, not copied into our YAML.
- Arcjet's `pattern.forbidden` continues feeding `verifiedbot.spoofed`
  (existing behavior); the spoofed-UA case stays a *negative-identity*
  surface, separate from the *expected-behavior* surface this spec adds.

### Cases handled

| Identity | Arcjet match? | Inherits | Result |
|---|---|---|---|
| StyloBot Internal | No (custom UA) | `_bot_type/internal` | rps 60 expected → behavior aligned → trusted-and-aligned clamp → RiskBand VeryLow |
| Googlebot at 5rps on `/sitemap.xml` | Yes (`googlebot`, SearchEngine) | `_bot_type/search-engine` + `googlebot.yaml` | aligned → Allow + Low |
| GPTBot at 200rps on `/admin/*` | Yes (`gptbot`, AiBot) | `_bot_type/ai-bot` | rate breach + path deviation → RiskBand lifts to Medium, deviated_slots=[`session.request_count`, `session.path_entropy`] |

## Evaluator

Single new file: `Risk/ArchetypeAlignmentEvaluator.cs`. Sealed, stateless,
input → output. Replaces the implicit drift-slot logic currently scattered
across `FingerprintNameComposer.GetVarianceTerm` and the centroid distance
machinery — those become consumers, not parallel implementations.

```csharp
public sealed record ArchetypeAlignmentResult
{
    public required string ArchetypeId { get; init; }
    public required IReadOnlyList<DimensionVerdict> Dimensions { get; init; }
    public required bool IdentityHolds { get; init; }
    public required bool BehaviorAligned { get; init; }
    public required double WeakDeviationScore { get; init; }   // 0..1 normalised
    public string? BreakAction { get; init; }                   // "split" when continuity rule violated
}

public sealed record DimensionVerdict
{
    public required string Slot { get; init; }                  // "session.request_count"
    public required object Expected { get; init; }
    public required object? Observed { get; init; }
    public required bool Aligned { get; init; }
    public required string DriftRole { get; init; }             // identity | behavior | weak
    public required double Distance { get; init; }              // 0..1 (0 = perfect)
    public string? Label { get; init; }                         // "rate shift", reuses GetVarianceTerm vocabulary
}
```

### Identity continuity (the MFS feed)

The evaluator tracks `identity_continuity.required` slots in a small
per-fingerprint sliding window of length `time_window`. When ALL required
slots are out-of-tolerance for `>=2` consecutive observations inside that
window, `BreakAction = "split"` is emitted and the `archetype.alignment.break_action`
signal fires. `MultiFactorSignatures.MatchAsync` is taught to read that
signal as a hint: instead of partial-match-rescuing the new shape, it treats
it as a candidate distinct fingerprint. No change to MFS's match algorithm
internals — just one new input that biases toward split.

### RiskBand wiring

`SignatureRiskInputs` gains three fields: `IdentityHolds`, `BehaviorAligned`,
`WeakDeviationScore`. `SignatureRiskVerdictComposer.Compose` gets one new
gating rule, inserted between friendly-pin and the raw-score bucket:

```csharp
// trusted-and-aligned clamp -- supersedes the immediate-fix Internal clamp
// at a2913eed. The immediate fix only checked BotType=Internal; this version
// adds the alignment guarantee so a compromised Internal client (UA spoofed
// to "StyloBot.Internal" but behavior-deviant) does NOT get the clamp.
else if ((inputs.BotType == nameof(BotType.Internal) || inputs.BotType == nameof(BotType.VerifiedBot))
         && inputs.IdentityHolds
         && inputs.BehaviorAligned)
{
    friendlyPin = true;
    friendlyWhy = $"{inputs.BotType}: identity holds + behavior aligned";
    reasons.Add($"trusted_and_aligned: {inputs.BotType}");
}
```

When identity holds but behavior is **NOT** aligned, the clamp does not fire
and the request follows the neutral path — that's the "Googlebot hitting
`/admin/login` at 200rps" case we WANT to flag.

## Per-signature pinning

DB-backed runtime override. Same dimension schema as YAML; the dashboard
serializes a `DimensionRule` record to JSON columns.

```sql
CREATE TABLE signature_pins (
    primary_signature TEXT NOT NULL,
    dimension_slot    TEXT NOT NULL,
    expected_json     JSONB NOT NULL,         -- {"value": 60}, {"list": ["GB","US"]}, etc.
    tolerance_json    JSONB NOT NULL,         -- {"shape": "range", "lower": 5, "upper": 500}
    drift_role        VARCHAR(16) NOT NULL,   -- identity | behavior | weak
    weight            DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by        VARCHAR(64),            -- operator id
    expires_at        TIMESTAMPTZ,            -- NULL = no expiry
    PRIMARY KEY (primary_signature, dimension_slot)
);
```

Read path: `IFingerprintStore.GetSignaturePinsAsync(primarySignature)`,
LFU-cached identically to existing signature aggregate. The evaluator's
compose step layers pins on top of Tier 1+2 inherited archetype.

Write path: a dashboard route POSTs to `/api/v1/signatures/{sig}/pin` with
`{slot, expected, tolerance, drift_role, weight, expires_in_days}`. Audited
in the existing `signature_labels` history table (column reuse) so each pin
is attributable.

## UI panel — signature detail

New section above the existing "Detection Signals" panel, titled **Expected
Behavior**. Two columns: declared expectations (from the composed archetype)
on the left, current observed on the right. Per-dimension row:

```
SLOT                                  EXPECTED         OBSERVED      DELTA
session.request_count                 60 (5–500)       58 rps        aligned ✓
session.frequency_periodicity_score   0.95 ±0.10       0.93          aligned ✓
network.country_code                  GB | US          GB            aligned ✓
hdr.ua_family                         StyloBot.Internal StyloBot...  aligned ✓ [identity]

DIMENSION                             STATUS
identity_holds                        true     (3 of 3 identity slots aligned)
behavior_aligned                      true     (4 of 4 behavior slots within tolerance)
break_action                          —
```

Each row links to a per-slot drilldown showing the last N observations vs
the expected envelope (sparkline). Identity slots are visually pinned
(small lock icon). Operator action: "Pin this dimension for this signature"
opens an inline form prefilling the current observed; writes a Tier 3 row.

A small "Vendor docs" link pulled live from the Arcjet entry's `.url`
(StyloBot Internal doesn't have one — shows "—").

Same panel is read-only on the home "Your Detection" card (collapsed
view: aligned/deviated counts only).

## Migration

1. **Loader extension** — `IdentityArchetypeRegistry.Load` reads either
   shorthand or long-form; defaults applied to shorthand.
2. **Seed Tier 1** — author 7 `_bot_type/*.yaml` files with seed values
   above. Tier 1 files load FIRST so subsequent tiers can inherit.
3. **Inheritance composer** — new `ComposedArchetype` record built once per
   request from (Tier 1 + Tier 2 + Tier 3). LFU-cached per primary_signature.
4. **Evaluator wired into pipeline** — runs after the existing archetype
   matcher decides which archetype anchors. Signals emitted to blackboard.
5. **Composer reads new fields** — `SignatureRiskVerdictComposer` consumes
   `IdentityHolds` + `BehaviorAligned` + clamp rule supersedes the
   immediate-fix Internal-only clamp.
6. **MultiFactorSignatures** reads `archetype.alignment.break_action` as a
   split-bias hint. Existing matching algorithm unchanged.
7. **DB schema** — `signature_pins` table added via the v7 migration
   infrastructure (per memory `project_v7_migration_infrastructure`); both
   sqlite and postgres backends.
8. **UI panel** — Razor view component `SbExpectedBehavior`, mounted in
   `_SignatureDetail.cshtml` above the Detection Signals panel.
9. **Remove the immediate-fix clamp** — once `IdentityHolds &&
   BehaviorAligned` is wired, delete the Internal-only branch added at
   a2913eed and the test that pins it; the new clamp covers the same case
   AND prevents the spoofed-Internal hole.

## Testing

- **Loader tests** — shorthand and long-form parse correctly; defaults
  applied; inheritance composes; circular `inherits_from` rejected; missing
  parent rejected with a clear error.
- **Evaluator unit tests** — one per tolerance shape × one per drift role
  (12 cases). Identity-continuity sliding-window window-edge cases.
- **Composer integration tests** — Internal+aligned clamps to VeryLow;
  Internal+behavior-deviant does NOT clamp; spoofed-UA-Internal (identity
  slot missing) does NOT clamp.
- **MFS integration tests** — `break_action=split` hint biases toward split;
  absence of hint preserves current matching behavior.
- **Per-signature pin tests** — Tier 3 overrides Tier 2 at the dimension
  level, not the whole-archetype level. Expired pins ignored. Pin write
  appends to `signature_labels` audit.
- **UI snapshot tests** — panel renders with deviated slots highlighted;
  vendor docs link present when Arcjet entry has `url`, hidden otherwise.

## Out of scope (this spec)

- Time-of-day expectations (`time_window: business_hours | quiet_hours`).
  Useful for cron-bot expectations, but no scheduling infra exists yet to
  consume them. Deferred until there's a second consumer.
- Response-code shape (`expected_status_distribution`). The existing
  endpoint analytics already surface this per endpoint; cross-cutting it
  per signature is a downstream join, not a new dimension.
- Cross-fingerprint expectation sharing (e.g., "all GPTBot variants share
  these expectations"). Tier 1+2 inheritance covers the common case; cross-
  cutting wait until a real use case appears.

## Configurable settings

Per `feedback_all_settings_configurable`, every threshold is exposed on
`ArchetypeAlignmentOptions`:

- `DefaultExactCaseSensitive` (bool, default true)
- `DefaultRangeInclusive` (bool, default true)
- `DefaultNumericDeltaShape` (`absolute` | `relative`, default `absolute`)
- `DefaultWeakWeight` (double, default 0.3)
- `IdentityContinuityMinViolations` (int, default 2) — how many consecutive
  out-of-tolerance observations inside `time_window` trigger `break_action`
- `WeakDeviationLowGate` / `WeakDeviationMediumGate` — RiskBand lift
  thresholds for weak-role drift accumulation
- `BotTypeDefaultArchetypeRoot` (path, default `_bot_type`) — Tier 1 folder
- `PinTableMaxAgeDays` (int, default 365) — bound on `expires_at`
- `EvaluatorEnabled` (bool, default true) — global kill switch

## Relationship to existing systems

| System | Today | After this spec |
|---|---|---|
| `IdentityArchetypeRegistry` | Header-only YAML, equality match | Any BDF dimension, tolerance shapes |
| `FingerprintNameComposer.GetVarianceTerm` | Hard-coded slot → label switch | Reads `DimensionVerdict.Label` from evaluator |
| `SignatureRiskVerdictComposer` | Internal-clamp hard-coded (a2913eed) | Reads `IdentityHolds`/`BehaviorAligned` (delete a2913eed branch) |
| `MultiFactorSignatures` | Partial-match always rescues | Reads `break_action` hint as split bias |
| `WellKnownBotIndex` (Arcjet) | Identity catalog (unchanged) | Source of `BotType` for Tier 1 inheritance |
| `SignatureDetail.cshtml` | Detection Signals panel only | Adds Expected Behavior panel above it |
| `IFingerprintStore` LFU | Reads aggregate + name history | Adds `GetSignaturePinsAsync` read |

## Risks

- **Tolerance-tuning churn** — wrong defaults will spam "drift" reports.
  Mitigated by: (a) seed values are conservative ranges, not point estimates;
  (b) per-tolerance-shape defaults configurable; (c) `drift_role: weak`
  doesn't lift RiskBand, only confidence — so noisy weak signals are
  quiet by default.
- **Continuity-rule false splits** — a legitimate browser update that
  changes `transport.tls_ja4` should NOT split. Mitigated by: only
  `drift_role: identity` slots gate continuity, AND multiple consecutive
  out-of-tolerance observations are required (`IdentityContinuityMinViolations`,
  default 2).
- **Pin sprawl** — operators pinning everything per-signature. Mitigated by:
  default `expires_at` of 30 days from the dashboard editor; audit log;
  dashboard surfaces a "pinned dimensions count" badge per signature.
- **Performance** — extra dimension evaluation per request. The evaluator
  runs O(n) over composed archetype dimensions (typically 10–20). Centroid
  distance + drift slot computation already happens; we're consuming its
  output, not adding a second pass.

## Open questions (resolved during brainstorm)

- ~~Where does `expected_behavior` live?~~ → Extended archetype YAML, no
  parallel system.
- ~~Per-archetype vs per-bot-type defaults?~~ → 3-tier inheritance, Tier 1 =
  per-BotType, Tier 2 = per-bot, Tier 3 = per-signature.
- ~~Identity vs behavior vs weak?~~ → Three explicit drift roles, distinct
  downstream consumers.
- ~~Tolerance shape count?~~ → Four (exact, range, oneof, numeric_delta).
- ~~Arcjet integration?~~ → No copy; BotType inheritance + live URL
  reads from `WellKnownBotEntry`.

## Acceptance

- StyloBot Internal on dashboard shows **Risk Profile Low** (or VeryLow once
  the alignment evaluator ships).
- Signature detail page has an **Expected Behavior** panel showing per-slot
  expected vs observed, with deviation highlights.
- A test scenario where Googlebot's observed `session.path_entropy`
  deviates from the search-engine archetype lifts RiskBand to Medium and
  surfaces the deviated slot in the panel.
- Per-signature pin written from the dashboard composes correctly: a
  pin'd `session.request_count.expected = 200` for one signature overrides
  the search-engine Tier 1 default of `5`.
- All existing FOSS tests pass; Multi-Factor Signatures matching behavior
  is byte-identical when `break_action` is null (i.e. no archetype with
  identity_continuity rule).
