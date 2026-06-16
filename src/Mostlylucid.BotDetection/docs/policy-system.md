# Policy System

StyloBot has three policy-shaped concepts. They are deliberately distinct.

## Detection Policy

Defines HOW detection runs for a request: which detectors are active in which wave, what risk thresholds apply, whether to escalate to AI, what to do on internal failure, and whether to shed load.

Type: `Mostlylucid.BotDetection.Policies.DetectionPolicy`.

Key properties:

| Property | Default | Purpose |
| --- | --- | --- |
| `FastPathDetectors` | (built-in list) | Detectors that run in Wave 0 |
| `SlowPathDetectors` | (built-in list) | Detectors that run on escalation |
| `AiPathDetectors` | (empty) | Detectors that run only when `EscalateToAi` triggers |
| `UseFastPath` | `true` | Whether to short-circuit on fast-path verdict |
| `EarlyExitThreshold` | 0.3 | Below this risk, allow early exit |
| `ImmediateBlockThreshold` | 0.95 | Above this risk, block immediately |
| `MinConfidence` | 0.0 | Confidence gate for blocking decisions |
| `OnFailure` | `FailOpen` | What to do on internal pipeline failure |
| `LoadShed.DropFractionAtCritical` | 0.0 | Fraction of requests to skip at Critical load |
| `LoadShed.DropFractionAtHigh` | 0.0 | Fraction of requests to skip at High load |
| `Transitions` | (empty) | Per-condition action-policy escalation |

Built-in policy names (registered at startup): `default`, `demo`, `strict`, `relaxed`, `static`, `allowVerifiedBots`, `learning`, `yarpLearning`, `monitor`, `profile`, `api`, `fastWithOnnx`, `fastWithAi`.

## Action Policy

Defines WHAT to do with the verdict: block, throttle, challenge, log-only, redirect.

Type: `Mostlylucid.BotDetection.Actions.IActionPolicy`.

Built-in names: `block`, `block-hard`, `block-soft`, `throttle`, `throttle-stealth`, `throttle-tools`, `throttle-status`, `throttle-aggressive`, `rate-limit-search`, `rate-limit-ai`, `rate-limit-social`, `rate-limit-monitoring`, `challenge`, `redirect-honeypot`, `logonly`, `shadow`. The `rate-limit-*` family (6.8+) is token-bucket-based; see [`policy-defaults.md`](policy-defaults.md) for the per-`BotType` mapping and [`configuration-reference.md`](configuration-reference.md#adaptive-scaling) for the adaptive-scaling tier ladder.

A detection policy can demand an action policy via `Transitions[i].ActionPolicyName`. The endpoint-level `[BotAction("...")]` attribute can also pick an action policy independently of the detection policy.

## Failure Mode

Distinct from "verdict on bot detection." `FailureMode` covers what to do when detection itself fails (orchestrator exception, store unavailable, sidecar unreachable).

- `FailOpen` (default): allow the request through, no detection signals.
- `FailClosed`: return HTTP 503, short-circuit the pipeline.
- `LogOnly`: allow through, emit `X-StyloBot-Failed` header and structured log entry.

Set per-policy via `DetectionPolicy.OnFailure`. The sidecar middleware reads the same enum via `SidecarClientOptions.OnFailure`.

JSON example:

```json
"Policies": {
  "admin": { "OnFailure": "FailClosed" }
}
```

## Load Shed

At High or Critical pipeline load (as reported by `PipelineLoadSensor.CurrentBand`), skip detection on the configured fraction of requests. Defaults are zero (opt-in).

- `DropFractionAtHigh`: fraction (0.0 to 1.0) of requests to skip at `LoadBand.High`.
- `DropFractionAtCritical`: fraction (0.0 to 1.0) of requests to skip at `LoadBand.Critical`.

Decision is deterministic by request seed (`Connection.Id` hash), so retries land identically. Sheds emit `X-StyloBot-Shed: 1` header so operators can observe the shed rate.

JSON example:

```json
"Policies": {
  "high-volume": {
    "LoadShed": { "DropFractionAtHigh": 0.0, "DropFractionAtCritical": 0.05 }
  }
}
```

## Threshold precedence (read this when tuning)

Two layers exist today:

1. Per-policy `DetectionPolicy` thresholds (`EarlyExitThreshold`, `ImmediateBlockThreshold`, `MinConfidence`).
2. Per-transition `PolicyTransition` thresholds (`WhenRiskExceeds`, `WhenRiskBelow`) for multi-step action selection.

The legacy `BotDetectionOptions.BotThreshold`, `MinConfidenceToBlock`, and detector enable/disable booleans (`EnableUserAgentDetection`, `EnableHeaderAnalysis`, etc.) are deprecated and scheduled for removal in a future major release. Until then, customers using them should migrate to the per-policy equivalents. Documentation in `appsettings.json` examples now uses the per-policy form.

## Policy editor (7.5+)

The `SbPolicyStack` view component renders the policy stack for a given scope in three embed shapes (`Full`, `EffectiveOnly`, `StatusBadge`). Edit affordances (pencil, drag handle, + Add rule, kind selector) are controlled by the `IPolicyCanEditPolicy` seam:

```csharp
// In Mostlylucid.BotDetection.UI.Services
public interface IPolicyCanEditPolicy
{
    bool CanEdit(ClaimsPrincipal? user);
}
```

The FOSS default (`AlwaysReadOnlyPolicyCanEditPolicy`) always returns `false` - the dashboard is read-only by construction. The commercial overlay registers a license-aware implementation via `services.Replace(...)` that gates on license and the `dashboard-write` role.

The `IPolicyCanEditPolicy` check is UI-only (controls visibility of edit affordances). The actual security boundary is `[Authorize(Policy = AuthPolicies.DashboardWrite)]` on the commercial mutation API.

The policy editor supports 7 action kinds, each with a dedicated partial:

| Kind | Partial |
|------|---------|
| `block` | `_EditAction_Block` |
| `allow` | `_EditAction_Allow` |
| `throttle` | `_EditAction_Throttle` |
| `ratelimit` | `_EditAction_RateLimit` |
| `challenge` | `_EditAction_Challenge` |
| `tag` | `_EditAction_Tag` |
| `observe` | `_EditAction_Observe` |

The kind selector uses HTMX to swap the per-kind slot on change. The full action kind list is driven by `PolicyActionEditorViewPaths.KindsForSelector` - a single source of truth for both the dropdown and the dispatcher.

All dashboard URLs are routed through `IDashboardLinkResolver` (7.5+), which reads `StyloBotDashboardOptions.NavBasePath` (or falls back to `BasePath`) so links work regardless of the mount point.

## Pack disambiguation

Four "pack" types exist, deliberately separate:

| Pack | Purpose |
| --- | --- |
| `SimulationPack` (aka `HoneypotPack`) | Fake response content served to bots |
| `ReactionPack` (planned) | Adaptive policy escalation on upstream degradation signals |
| `CompliancePack` | Data retention, anonymization, DSAR audit |
| `MonitoringPack` | Metric collection spec |

`SimulationPack` and `HoneypotPack` refer to the same record type; `HoneypotPack` is a static helper class that exposes a `Create` factory for new code seeking the more-descriptive name.

## Existing capabilities you might be looking for

Several capabilities customers ask about already exist:

- **Per-detector timeout**: `DetectorDefaults.Timing.TimeoutMs` in each detector's YAML manifest. When exceeded, the orchestrator cancels that detector and moves on to the rest.
- **Per-detector circuit breaker**: `BotDetectionOptions.CircuitBreakerThreshold` (default 5) / `CircuitBreakerResetTime` (default 60s). When a detector fails N times in a row, it's skipped for the reset window, then probed via half-open state.
- **Adaptive load handling for background work**: `PipelineLoadSensor.LoadFactor` scales clustering and enrichment intervals as RPS climbs.
- **Out-of-process detection**: register `SidecarBotDetectionMiddleware` instead of the in-process one. The sidecar is a separate ASP.NET host (`Mostlylucid.BotDetection.Sidecar`).
- **Sampling for learning**: `FastPathDecider.ScheduledForFullAnalysis` lets uncertain fast-path verdicts be re-analysed asynchronously.

## Putting it together: a high-security policy

```json
"Policies": {
  "admin-panel": {
    "FastPathDetectors": ["..."],
    "SlowPathDetectors": ["..."],
    "ImmediateBlockThreshold": 0.7,
    "MinConfidence": 0.5,
    "OnFailure": "FailClosed"
  },
  "public-marketing": {
    "FastPathDetectors": ["..."],
    "EarlyExitThreshold": 0.5,
    "ImmediateBlockThreshold": 0.95,
    "OnFailure": "FailOpen",
    "LoadShed": { "DropFractionAtCritical": 0.10 }
  }
}
```

The `admin-panel` policy biases toward strict detection (low block threshold, requires high confidence, fails closed on internal error). The `public-marketing` policy biases toward availability (high block threshold, fails open on error, sheds 10% of requests at Critical load).
