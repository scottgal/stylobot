# Reaction Packs Design

Date: 2026-05-06

## Summary

Reaction Packs are named protection modes that activate automatically when StyloBot observes upstream degradation signals (5xx errors, 429 rate-limits, latency spikes). Each pack contains a set of stepped policies with explicit hysteresis-based escalation and de-escalation conditions. The result is adaptive traffic shaping that responds to real system health rather than fixed thresholds.

---

## Architecture

### Components

**`DegradationAtom`** (new, in ephemeral coordinator)

A pure observer. On every request completion it records the upstream response status code and latency into per-endpoint and global rolling windows. Emits signals into the existing signal stream:

- `response.error_rate_5xx` - global 5xx rate over the configured window
- `response.rate_429` - global 429 rate over the configured window
- `response.latency_p95` - global p95 latency in milliseconds
- Scoped variants: `response.error_rate_5xx:/api/checkout`, etc.

No knowledge of packs, policies, or escalation logic. All window sizes and signal keys are YAML-configured via `GetParam<T>()`.

**`ReactionPackEngine`** (new singleton service)

Owns the per-pack state machine. At startup, loads all `*.reaction-pack.yaml` definitions from embedded resources. On each signal update from the atom, evaluates hysteresis conditions for each pack and advances or retreats the active step level. Persists transition events to SQLite (FOSS) so the dashboard can show history.

Exposes `IReactionPackContext` for middleware consumption.

**`IReactionPackContext`** (new interface)

Single method: `string? GetOverridePolicy(string endpoint, string defaultPolicy)`.

- If no pack is active for this endpoint: returns `null` (caller uses default).
- If a pack is active: returns the policy name for the current step level.
- Zero allocation on the happy path.

**Middleware integration**

`BotDetectionMiddleware` already resolves its action policy by name. The only change: before the registry lookup it calls `IReactionPackContext.GetOverridePolicy`. If a non-null value comes back, that policy name is used instead of the per-request default. One line of change in the middleware.

**`ReactionPackDashboardService`** (new, in `BotDetection.UI`)

Reads active pack state and transition history from SQLite. Serves the dashboard Reaction Packs tab: active pack name, current level, signal sparklines, and a transition timeline.

---

## YAML Schema

Packs live in `Definitions/ReactionPacks/*.reaction-pack.yaml`, auto-included as embedded resources via the existing `*.yaml` glob in the `.csproj`.

```yaml
name: error-spike-protection
description: Activates when upstream error rate or 429s spike
enabled: true
scope: global          # "global" or "endpoint:/some/path"

# Which signals this pack watches (for dashboard display)
signals:
  - response.error_rate_5xx
  - response.rate_429
  - response.latency_p95

steps:
  - level: 1
    name: watch
    activate:
      condition: any   # any | all
      rules:
        - signal: response.error_rate_5xx
          above: 0.05
          for_seconds: 60
        - signal: response.rate_429
          above: 0.03
          for_seconds: 30
    policy: throttle-gentle
    deactivate:
      condition: all
      rules:
        - signal: response.error_rate_5xx
          below: 0.02
          for_seconds: 120
        - signal: response.rate_429
          below: 0.01
          for_seconds: 120

  - level: 2
    name: protect
    activate:
      condition: any
      rules:
        - signal: response.error_rate_5xx
          above: 0.15
          for_seconds: 30
        - signal: response.rate_429
          above: 0.10
          for_seconds: 20
    policy: throttle-aggressive
    deactivate:
      condition: all
      rules:
        - signal: response.error_rate_5xx
          below: 0.05
          for_seconds: 180

  - level: 3
    name: critical
    activate:
      condition: any
      rules:
        - signal: response.error_rate_5xx
          above: 0.30
          for_seconds: 15
    policy: block-soft
    deactivate:
      condition: all
      rules:
        - signal: response.error_rate_5xx
          below: 0.10
          for_seconds: 300
```

**Escalation** is sequential: level 1 must be active before level 2 can activate, etc.

**De-escalation** is direct: if level 3's deactivate conditions are met but level 2's are not, the engine steps down to level 2. If both are met, it steps to level 1. If level 1's are met, the pack deactivates entirely.

**`for_seconds`** is the hysteresis window. The signal must stay above/below the threshold for the full window before the engine acts. This prevents flapping on transient spikes.

---

## Signal Groups

Signal groups are named sets of signal keys defined in YAML and referenceable anywhere signals appear - reaction packs, detector manifests, and (at the StyloFlow level) wave trigger conditions. They eliminate repetition and make pack YAML readable.

Groups live in `Definitions/SignalGroups/*.signal-group.yaml`:

```yaml
name: upstream-health
description: Core upstream response health signals
signals:
  - response.error_rate_5xx
  - response.rate_429
  - response.latency_p95

---

name: checkout-health
description: Checkout endpoint health signals
signals:
  - response.error_rate_5xx:/api/checkout
  - response.rate_429:/api/checkout
  - response.latency_p95:/api/checkout
```

Reaction packs reference groups with a `$` prefix:

```yaml
signals: $upstream-health

steps:
  - level: 1
    name: watch
    activate:
      condition: any
      rules:
        - signal_group: $upstream-health
          above: 0.05
          for_seconds: 60
```

When a rule references a signal group, the condition evaluates as `any` or `all` signals in the group crossing the threshold (controlled by the rule's own `condition` field, defaulting to `any`).

**`ISignalGroupRegistry`** (new interface, StyloFlow-level) resolves group names to signal key lists at startup. All YAML loaders (detector manifests, reaction packs) expand group references during deserialization. No runtime resolution cost.

New files:

| File | Purpose |
|------|---------|
| `BotDetection/Models/SignalGroupDefinition.cs` | YAML deserialization model |
| `BotDetection/Services/SignalGroupRegistry.cs` | `ISignalGroupRegistry` implementation |
| `BotDetection/Definitions/SignalGroups/upstream-health.signal-group.yaml` | Built-in group |
| `BotDetection/Definitions/SignalGroups/checkout-health.signal-group.yaml` | Built-in group |

---

## Built-in Packs (shipped with the product)

Three packs ship as embedded YAML. All thresholds come from YAML parameters - no magic numbers.

**`error-spike-protection.reaction-pack.yaml`**
Watches `response.error_rate_5xx` and `response.rate_429`. Three levels: watch (throttle-gentle), protect (throttle-aggressive), critical (block-soft). Global scope.

**`latency-protection.reaction-pack.yaml`**
Watches `response.latency_p95`. Two levels: watch (throttle-gentle), protect (throttle-moderate). Global scope. Useful for backends that slow down under scraper load without necessarily returning errors.

**`checkout-protection.reaction-pack.yaml`**
Watches `response.rate_429:/api/checkout` and `response.error_rate_5xx:/api/checkout`. Two levels: protect (challenge-pow), critical (block-soft). Endpoint-scoped. Demonstrates per-path packs.

---

## Signal Windows and Evaluation

The `DegradationAtom` maintains a lock-free sliding window per signal key. Window size is configurable per signal in YAML (default: 60 seconds). The atom uses the same EMA approach as `PipelineLoadSensor` for zero-allocation rate computation on the hot path.

The `ReactionPackEngine` evaluates all pack conditions every N seconds (configurable, default: 5 seconds) rather than on every request. This decouples evaluation from request rate and means evaluation cost is O(packs * steps * rules) regardless of traffic volume.

Hysteresis tracking: for each rule the engine stores the timestamp when the condition first became true. The condition is only considered satisfied once `now - first_true_time >= for_seconds`. If the signal crosses back before the window expires, the timer resets.

---

## Persistence (SQLite, FOSS)

New table: `reaction_pack_transitions`

| column | type | description |
|--------|------|-------------|
| id | integer PK | |
| pack_name | text | |
| from_level | integer | 0 = inactive |
| to_level | integer | 0 = deactivated |
| triggered_by | text | signal key that crossed threshold |
| signal_value | real | signal value at transition time |
| occurred_at | integer | Unix timestamp |

Current active state is derived by reading the latest transition per pack. No separate state table.

---

## Dashboard

New **Reaction Packs** tab in `/_stylobot` showing:

- Active packs: name, current level name, active policy, how long active
- Signal sparklines for each watched signal (last 10 minutes)
- Transition timeline: when packs escalated/de-escalated and what triggered each transition
- Inactive packs: current signal values vs their level-1 activation thresholds (so operators can see how close to triggering)

FOSS: view-only. Commercial: add/edit pack YAML via dashboard form, override thresholds per deployment without editing files.

---

## Data Flow

```
Request in
  └── DegradationAtom.RecordRequest()        # records nothing yet

Response complete (middleware finally block)
  └── DegradationAtom.RecordResponse(status, latencyMs, path)
        └── updates rolling windows
        └── emits updated signal values

ReactionPackEngine (background timer, every 5s)
  └── reads current signal values from DegradationAtom
  └── evaluates hysteresis conditions per pack per step
  └── if transition: updates IReactionPackContext + persists to SQLite

Next request in
  └── BotDetectionMiddleware
        └── IReactionPackContext.GetOverridePolicy(path, defaultPolicy)
        └── IActionPolicyRegistry.GetPolicy(resolvedName)
        └── policy.ExecuteAsync(...)
```

---

## New Files

| File | Purpose |
|------|---------|
| `BotDetection/Services/DegradationAtom.cs` | Rolling window signal emitter |
| `BotDetection/Services/ReactionPackEngine.cs` | State machine, hysteresis evaluation |
| `BotDetection/Services/IReactionPackContext.cs` | Middleware query interface |
| `BotDetection/Models/ReactionPackDefinition.cs` | YAML deserialization model |
| `BotDetection/Models/ReactionPackStep.cs` | Step model with activate/deactivate conditions |
| `BotDetection/Data/ReactionPackTransitionStore.cs` | SQLite persistence |
| `BotDetection/Definitions/ReactionPacks/error-spike-protection.reaction-pack.yaml` | Built-in pack |
| `BotDetection/Definitions/ReactionPacks/latency-protection.reaction-pack.yaml` | Built-in pack |
| `BotDetection/Definitions/ReactionPacks/checkout-protection.reaction-pack.yaml` | Built-in pack |
| `BotDetection.UI/Services/ReactionPackDashboardService.cs` | Dashboard data queries |

### Modified Files

| File | Change |
|------|--------|
| `BotDetection/Middleware/BotDetectionMiddleware.cs` | Call `IReactionPackContext.GetOverridePolicy` before registry lookup |
| `BotDetection/Extensions/ServiceCollectionExtensions.cs` | Register `DegradationAtom`, `ReactionPackEngine`, `IReactionPackContext` |
| `BotDetection/Data/SqliteSessionStore.cs` (or migration file) | Add `reaction_pack_transitions` table |
| `BotDetection.UI` | Add Reaction Packs tab + API endpoint |

---

## Pack Priority and Conflict Resolution

When multiple packs are active and both want to override the same endpoint's policy, the pack with the highest current step level wins. If two packs are at the same level, the pack with the more restrictive policy (highest ActionType severity: Block > Challenge > Throttle > LogOnly) wins.

Pack priority is also configurable via an optional `priority` field in YAML (higher integer = higher priority). Default priority is 0. This allows operators to pin a critical-path pack above a global one without relying on level comparison.

```yaml
name: checkout-protection
priority: 10    # wins over default-priority packs at the same level
```

---

## Out of Scope (this iteration)

- Pipeline tuning (skipping slow-path detectors under load) - hooks will be added later
- Commercial live-edit of pack YAML via dashboard
- Per-bot-type policy overrides within a pack (global and per-endpoint only for now)
