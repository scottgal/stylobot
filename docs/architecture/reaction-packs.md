# Reaction Packs: adaptive response escalation

> **Status:** concept captured; primitives shipped, full engine deferred.
> The reusable core (degradation EMA + hysteresis + a rate-limit tier ladder)
> is live in `main`. The general multi-action escalation engine was prototyped
> on [PR #16](https://github.com/scottgal/stylobot/pull/16)
> (branch `feature/reaction-packs`) and deliberately **not** merged. This doc
> exists so the design survives that branch being deleted, and so a future
> revival is a lookup, not an archaeology dig.

## The concept

A **reaction pack** is adaptive *response* escalation (distinct from bot
*detection*): it watches the health of the origin/upstream and automatically
tightens the protection posture when things go bad, then relaxes when they
recover. Detection answers "is this a bot?"; a reaction pack answers "given
the system is degrading, how hard should we push back right now?"

The input is upstream health, not a single request's bot score:

- `response.error_rate_5xx` — rolling fraction of recent responses returning 5xx
- `response.rate_429` — rolling fraction returning 429
- `response.latency_ema` — EMA of response latency (ms)

These are emitted globally and per-endpoint-prefix. A pack defines a ladder of
**levels**, each with an `activate` / `deactivate` rule set gated by
**hysteresis** (enter fast on a short window, leave slow on a long window) so
the posture doesn't flap. While a level is hot, the pack **overrides the action
policy** for its scope (global, or an endpoint prefix).

## What shipped in `main` (the salvaged primitives)

The doc comment on `DegradationAtom` says it plainly: *"the pack framework
around it was over-engineered for our needs, but the EMA primitive itself is a
clean fit for adaptive rate-limit scaling."* So the load-bearing pieces were
lifted out and the framework was left behind:

| Piece | Location | Role |
|-------|----------|------|
| `DegradationAtom` | `RateLimit/DegradationAtom.cs` | Rolling EMA of 5xx / 429 / latency. The degradation signal source. |
| `HysteresisTracker` | `RateLimit/HysteresisTracker.cs` | Enter-fast / leave-slow gate so tier transitions don't flap. |
| `AdaptiveScalingTracker` | `RateLimit/AdaptiveScalingTracker.cs` | The one surviving *response lever*: a tier ladder (`nominal` → `degraded`) that scales the bot rate-limit multiplier from origin health. |
| `AdaptiveScalingOptions` | `RateLimit/AdaptiveScalingOptions.cs` | The tier table (P95 latency / 5xx% thresholds → bot multiplier). |

So the **core idea is alive** — degradation-driven, hysteresis-gated, automatic
escalation — but narrowed to a single lever: it scales the rate-limit allowance.
It does **not** escalate across response *kinds* (e.g. step up to `challenge` or
`block` under load), and it is not operator-configurable per endpoint as packs.

## What was deferred (the full engine, on PR #16)

Everything that generalized the one lever into a configurable, multi-action,
distributable, observable subsystem:

- **`ReactionPackEngine`** — the general multi-step escalation state machine
  (`Services/ReactionPackEngine.cs`, `ReactionRuleEvaluator.cs`,
  `ReactionPackContext.cs`).
- **YAML reaction packs** — `*.reaction-pack.yaml` defining a `scope`,
  `priority`, and a `steps` ladder of `level` → `activate`/`deactivate` rules →
  `policy`. Built-ins: `checkout-protection`, `error-spike-protection`,
  `latency-protection`. Example shape:

  ```yaml
  name: checkout-protection
  scope: endpoint:/api/checkout
  priority: 10
  steps:
    - level: 1
      activate: { condition: any, rules: [ { signal: response.rate_429:/api/checkout, above: 0.10, for_seconds: 30 } ] }
      policy: challenge-pow
      deactivate: { condition: all, rules: [ { signal: response.rate_429:/api/checkout, below: 0.03, for_seconds: 180 } ] }
    - level: 2
      activate: { condition: any, rules: [ { signal: response.error_rate_5xx:/api/checkout, above: 0.30, for_seconds: 15 } ] }
      policy: block-soft
      deactivate: { condition: all, rules: [ { signal: response.error_rate_5xx:/api/checkout, below: 0.10, for_seconds: 300 } ] }
  ```

- **Hook interfaces** — `IStylobotPreActionHook` (per-request action-policy
  override) and `IStylobotPostResponseHook` (fire-and-forget response telemetry),
  wired into `BotDetectionMiddleware` via `PackRegistry<T>` (write-once
  `ImmutableArray<T>` at startup, zero-cost per-request reads).
- **`.stylopack` distribution** — `PackLoader` / `PackManifest` loading
  zip-packaged packs with `foss` / `commercial` tier gating;
  `BuiltinPackPopulator` wiring the defaults at startup.
- **SQLite persistence** — `ReactionPackTransitionStore` recording every level
  transition.
- **Dashboard tab** — a "Reaction Packs" tab (`_ReactionPacksTab.cshtml` +
  `ReactionPackDashboardService`) showing active levels, configured-but-inactive
  packs, and transition history.

## The dormant seam in `main`

One thread of the full engine is intentionally left dangling so the dashboard
can light up the moment an engine is reintroduced:

- [`Services/IReactionPackContext.cs`](../../src/Mostlylucid.BotDetection/Services/IReactionPackContext.cs)
  — a typed contract (`GetOverridePolicy` + `GetActiveStates() →
  IReadOnlyList<ReactionPackState>`). **Nothing registers it**, so
  `GetService<IReactionPackContext>()` is null and the dashboard's per-endpoint
  pack-coverage panel stays hidden.
- `StyloBotDashboardMiddleware.BuildEndpointDetailCoverage` consumes that
  interface directly. (It previously probed for the type by string via
  reflection — and read named properties off a value-tuple that doesn't expose
  them, so it could never have worked even if the PR had merged. That has been
  replaced with the typed call.)

To revive the panel, register an `IReactionPackContext` implementation; no
dashboard change is needed.

## Reviving the full engine

If/when the general escalation engine earns its keep:

1. The implementation plan is on the PR branch at
   `docs/superpowers/plans/2026-05-06-pack-system.md` (full task-by-task file map).
2. Reuse `DegradationAtom` + `HysteresisTracker` as-is — they are the engine's
   signal source and flap-guard; don't re-derive them.
3. Implement `IReactionPackContext` (already in core) as the engine's runtime
   state; the dashboard coverage panel wires up for free.
4. Decide the distribution question deliberately: the `.stylopack` loader +
   tier gating was the heaviest deferred piece. The engine does not need it —
   embedded built-in YAML (as the simulation packs already do) is enough for
   FOSS; `.stylopack` is a commercial concern.

## Related

- [`signal-contracts.md`](signal-contracts.md) — foundation vs. classifier
  signal contract (reaction packs consume `response.*` signals, they don't
  produce detection signals).
- `RateLimit/AdaptiveScalingTracker.cs` — the shipped, narrowed form of this concept.
