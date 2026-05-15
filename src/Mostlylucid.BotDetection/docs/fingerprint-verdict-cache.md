# Fingerprint Verdict Cache

Reference documentation for the per-request gate that reuses prior verdicts on
known fingerprints. Pairs with `signature-coordinator-architecture.md` (which
describes the sliding window itself) and `policy-system.md` (which describes
how `SignatureCacheOptions` is bound per policy).

> **6.4.7+:** when the metastable identity layer ([`identity-fingerprint-match.md`](identity-fingerprint-match.md))
> is enabled, the gate composes the per-signature aggregate with the per-fingerprint
> cached verdict — the fresher of the two sources wins. Because the fingerprint
> source survives IP+UA rotation, a returning visitor whose primary signature has
> changed inherits their prior verdict instead of paying for a fresh pipeline pass.
> Skip-path responses set `X-StyloBot-VerdictSource: identity-cache` (vs. the plain
> `cache` for signature-aggregate hits) and emit `X-StyloBot-IdentityFingerprint`
> with the resolved fingerprint id.

## 1. The scaling thesis

A bot fingerprint visiting 50 times in 5 minutes does not need 50 full pipeline
runs to be classified. The first request earns a verdict; the next 49 can reuse
it. Likewise an established human fingerprint should not pay for 49 detection
re-runs of the same answer. The fingerprint verdict gate makes this concrete:
known fingerprints reuse their verdict, only unknown, stale, or
verifiably-changed fingerprints run the full pipeline.

The verdict source is the existing ephemeral sliding window inside
`SignatureCoordinator`, not a parallel cache. Per-signature aggregates are
EWMA-smoothed (one-off spikes do not pin a signature) and the window is bounded
by `MaxSignaturesInWindow` (default 1000, LRU + TTL eviction). The cost of
detection shifts from "per request" to "per new or changing fingerprint".

## 2. The four gate outcomes

The gate runs in `BotDetectionMiddleware` before the orchestrator. Given the
request's primary signature and the policy's `SignatureCacheOptions`, it
returns one of three actions; a fourth outcome (Watchdog-trip) emerges when a
Skip is vetoed.

### Skip

Fires when the live verdict from `SignatureCoordinator.TryGetVerdictAsync`
meets `SkipMinConfidence` AND its `LastSeenUtc` is within `SkipMaxAgeSeconds`
AND `VarianceWatchdog.CheckAsync` does not trip.

The request bypasses the detector pipeline. An `AggregatedEvidence` is built
directly from the cached `SignatureVerdict` (`BotProbability`, `Confidence`,
`RiskBand`), `RequestContributionDelta` is set to zero (the request did no
per-request work, so the prior equals the posterior), and the response header
`X-StyloBot-VerdictSource: cache` is emitted.

`SignatureCoordinator.NotifyObservationAsync` and
`VarianceWatchdog.RecordObservationAsync` are still called on the Skip path so
clustering, drift detection, dashboard counters, and the watchdog's own
per-fingerprint history continue to see the request (see section 7).

### Watchdog-trip

Fires when a request would have been a Skip but `VarianceWatchdog` detected an
unusual signal: the same fingerprint hitting from a new /24 within
`IpRotationWindowSeconds`, or a request-rate spike beyond
`RateSpikeMultiplier` of the rolling baseline.

The Skip is cancelled. The middleware emits
`X-StyloBot-VerdictSource: pipeline` and `X-StyloBot-WatchdogTrip:
<reason>` (e.g. `ip-rotation:10.0.5.0/24->10.0.7.0/24`,
`rate-spike:42.0/4.0`), then falls through to the full pipeline. The cached
verdict is not enforced; this request gets a fresh evaluation.

### Bias

Fires when the live verdict meets `BiasMinConfidence` but does not qualify for
Skip (either confidence is below `SkipMinConfidence` or the verdict is older
than `SkipMaxAgeSeconds` but still within `BiasMaxAgeSeconds`). Also fires on
the `SkipSamplingRate` refresh fraction (see section 5).

The middleware stashes `fingerprint.prior.probability`,
`fingerprint.prior.confidence`, and `fingerprint.prior.age_seconds` on
`HttpContext.Items`. The Wave 0 `FingerprintPriorContributor` reads them and
emits a single `DetectionContribution` with `ConfidenceDelta = 2 * (prob - 0.5)`
and `Weight = prior_confidence * prior_weight_multiplier * linear-age-decay`,
so a recent confident prior anchors the posterior strongly while an old prior
fades to zero weight. The full pipeline still runs alongside it.

### Miss

Fires when there is no usable verdict: cold fingerprint (first observation),
verdict below `BiasMinConfidence`, or older than `BiasMaxAgeSeconds`. Also
fires when `SignatureCache.Enabled` is false on the policy, or when no primary
signature has been computed.

The request runs the full pipeline with no prior. `PriorProbability` and
`RequestContributionDelta` on the resulting `AggregatedEvidence` are both
zero. No special response header is emitted.

## 3. Per-policy configuration

`SignatureCacheOptions` lives on each `DetectionPolicy`. Defaults are tuned for
general-purpose sites; admin and high-security paths typically disable the
gate entirely.

```json
"BotDetection": {
  "Policies": {
    "default": {
      "SignatureCache": {
        "Enabled": true,
        "SkipMinConfidence": 0.85,
        "SkipMaxAgeSeconds": 300,
        "BiasMinConfidence": 0.30,
        "BiasMaxAgeSeconds": 86400,
        "SkipSamplingRate": 0.05,
        "Watchdog": {
          "Enabled": true,
          "IpRotationWindowSeconds": 300,
          "RateSpikeMultiplier": 10.0,
          "CheckPathCentroid": true
        }
      }
    },
    "admin-panel": {
      "SignatureCache": { "Enabled": false }
    },
    "marketing-pages": {
      "SignatureCache": {
        "SkipMinConfidence": 0.75,
        "SkipMaxAgeSeconds": 900,
        "SkipSamplingRate": 0.02
      }
    }
  }
}
```

Two patterns recur:

- "Trust the cache aggressively" (marketing, public docs, static content): a
  lower `SkipMinConfidence` (~0.75), longer `SkipMaxAgeSeconds` (~15 minutes),
  smaller `SkipSamplingRate` (~0.02). Most repeat visitors fast-path.
- "Never trust the cache" (admin, login, billing): `Enabled = false`. Every
  request runs the full pipeline regardless of prior reputation.

## 4. The EWMA upsert and recency

Before this work, the `signatures.bot_probability` column was updated by `MAX`:
a single 0.95 false positive pinned the signature at 0.95 forever, and only
later writes that exceeded 0.95 could move it. The result was a one-way
ratchet that the cache then served back as a confident verdict.

The persistence upsert is now an EWMA driven by
`BotDetectionOptions.SignatureEwmaAlpha` (default 0.15):

```
new = alpha * observation + (1 - alpha) * previous
```

Alpha 0.10 retains roughly 90 percent of prior state per write (strong memory,
slow reaction). Alpha 0.30 reacts more quickly but is noisier. The default 0.15
is the conservative midpoint.

The `signatures` table also gained `last_updated_utc`. `SignatureVerdict`
carries it forward to the gate as `LastSeenUtc` so freshness decisions
(`SkipMaxAgeSeconds`, `BiasMaxAgeSeconds`) work against actual write times,
not against approximations from in-memory state.

## 5. Skip is direction-agnostic

`SkipMinConfidence` is about confidence, not about which side. A sure-bot AND
a sure-human both qualify for Skip when their probability is far from 0.5 and
their confidence is high. A known-human fingerprint visiting from a known IP
gets fast-pathed in exactly the same way as a known-bot.

This matters in practice: most repeat visitors on a public site are humans,
not bots, and most of the cost saving from the gate comes from skipping their
pipeline runs. Treating Skip as "block-cache-only" would miss the larger
optimisation. The `SignatureVerdict` carries a `RiskBand` and the cached
`AggregatedEvidence` reproduces it, so action policies still see whether the
fast-pathed request is human-trusted or bot-blocked.

`SkipSamplingRate` (default 0.05) forces a deterministic 5 percent of
Skip-eligible requests through the full pipeline anyway, so the cached
verdicts stay honest as the fingerprint's behaviour drifts. The sampling
function hashes the signature so retries from the same client land
identically (no double pipeline runs on retry storms).

## 6. The sliding window is the core, not a cache

`SignatureCoordinator` already maintains per-signature state across requests:
`SlidingCacheAtom`-backed, bounded by `MaxSignaturesInWindow` (default 1000,
configurable), LRU + TTL eviction, request counts, latest risk band, EWMA
posterior. Every previous design version of this work proposed a parallel
"verdict cache" sitting alongside it.

The decision was to NOT build one. A parallel cache would duplicate the
coordinator's state and force an invalidation protocol between them
(coordinator updates -> cache writes; cache reads -> coordinator stays
consistent). The gate instead calls `SignatureCoordinator.TryGetVerdictAsync`,
which returns an immutable `SignatureVerdict` snapshot of whatever the
coordinator currently holds. There is exactly one source of truth.

Two consequences:

- The gate inherits the coordinator's eviction policy. Signatures that fall
  out of the LRU bucket simply produce a `Miss`, and the next request rebuilds
  them. No stale-cache problem.
- `MaxSignaturesInWindow` is the gate's effective capacity. Tuning it (per
  platform tier, or in `appsettings.json`) tunes both the coordinator and the
  gate together.

## 7. Sliding-window observation on Skip

Skipping the pipeline does NOT skip observation. On the Skip path the
middleware still calls:

- `SignatureCoordinator.NotifyObservationAsync(signature, path, botProb)` so
  the coordinator's per-signature request count, path frequency, and recent
  EWMA stay current.
- `VarianceWatchdog.RecordObservationAsync(signature, ip, path)` so the
  watchdog's per-fingerprint IP/rate history reflects the request.

What is bypassed is detector work and policy aggregation. What is preserved is
the cross-request telemetry that clustering, drift detection, dashboard
counters, and the watchdog itself depend on. The trade is intentional: trust
the cached verdict for the policy decision, but keep feeding the window so
future decisions stay grounded.

This also means the dashboard's per-fingerprint counters do not collapse when
the gate is doing its job. A Skip-heavy traffic mix still shows accurate
request counts, paths, and timing for each fingerprint; only the detector
contribution counts are lower (and that, intentionally, is the whole point).

## 8. Per-request contribution delta

`AggregatedEvidence` gained two fields: `PriorProbability` (the cached
verdict's `BotProbability` that was applied to the request, or zero on Miss
and cold-start) and `RequestContributionDelta` (posterior minus prior, i.e.
how much this single request moved the fingerprint's belief).

The orchestrator computes them by reverse-mapping the `FingerprintPrior`
contribution: that contributor emits `ConfidenceDelta = 2 * (prob - 0.5)`,
so the inverse gives back the prior probability for display, independent of
how the orchestrator weighted it among other contributors.

The CLI dashboard surfaces `RequestContributionDelta` as a signed
percentage-point figure on each request row. This fixes the "30 percent to 38
percent looks hysterical" experience that the old standalone-score view
produced: a request that nudged a fingerprint's posterior from 0.30 to 0.38 is
not an 80-percent-bot request; it is a +0.08 nudge on a fingerprint that was
already at 0.30. Rows on the Skip path show a dim asterisk and zero delta
(the request did not run detectors; the cache answered).

The fingerprint sidebar (Top Fingerprints) shows the EWMA posterior and an
8-sample sparkline of recent observations, so the trend behind any
high-delta row is one glance away.

## 9. Performance posture

The intended cost saving is direct. An established human fingerprint visiting
50 times in 5 minutes runs:

- The first request: full pipeline (Miss).
- Approximately 5 percent of the remaining 49, per `SkipSamplingRate`: full
  pipeline (refresh).
- The other ~47: Skip path, answered from the in-memory sliding window in
  microseconds.

Net: ~3 pipeline runs instead of 50, roughly a 94 percent reduction for that
fingerprint. The exact figures depend on `SkipMinConfidence`,
`SkipMaxAgeSeconds`, and `SkipSamplingRate`; the qualitative shape is the
same: fewer pipeline runs proportional to repeat-visit ratio.

Cold-start traffic (every fingerprint is new) sees no saving. The cache is
only useful when fingerprints recur, which is the normal case once a site
has warmed up.

## 10. Disabling and tuning

When to set `Enabled = false`:
- Admin panels, billing, login, password reset, account settings.
- Anywhere a fingerprint reusing a verdict could be exploited.
- Test environments where you want every request through the pipeline.

When to lower `SkipMaxAgeSeconds`:
- High-risk pages where a cached verdict older than ~60 seconds is suspect.
- Endpoints where bot behaviour is known to morph fast (CVE probe paths,
  honeypot endpoints).

When to raise `BiasMaxAgeSeconds`:
- Stable public content where a 24-hour-old prior is still useful as a hint,
  even if the pipeline reruns.
- Endpoints with low traffic per fingerprint, where rebuilding the prior from
  scratch is expensive.

When to disable the watchdog (`Watchdog.Enabled = false`):
- Test environments where deterministic Skip is required.
- Behind a CDN where remote IP rotation is meaningless (the watchdog's
  IP-rotation check still has signal here if you preserve client IP, but
  disable it if you do not).

When to lower `RateSpikeMultiplier`:
- High-security endpoints where a fingerprint suddenly doing 10x its normal
  rate should be re-evaluated, not Skip-served.

The general pattern: trust the cache aggressively for marketing and public
traffic, run the full pipeline always for admin and authentication. The Bias
path is the middle ground: prior is still applied, pipeline still runs,
result is anchored without being short-circuited.

## 11. What is not done yet

Four follow-ups remain after this branch lands:

- `VarianceWatchdogOptions.CheckPathCentroid` is wired into the options record
  but the actual check is deferred. It needs `CentroidSequenceStore` plumbed
  through the watchdog to compare the requested path's `RequestState` against
  the fingerprint's expected centroid set. The option default is `true` so
  enabling it is a one-line change once the wiring lands.
- Entity-resolution multi-signature merge priors. The `entities` table exists
  and records merged signatures across fingerprint rotations, but the gate
  does not currently consult it. A future iteration will look up the entity
  for a cold signature and inherit its verdict as a prior on the first
  request rather than waiting for the per-signature window to build up.
- `BoundedChannelLearningBusTests.TryPublish_WhenQueueFull_DropsOldestAndAcceptsNew`
  still flakes on slow CI runners. The same family of fix used in 6.4.2
  (sequential observation rather than concurrent stress) is the obvious
  cleanup; it just was not in scope here.
- Cloudflare anonymous tunnel timeout detection in the CLI dashboard. The
  dashboard currently hangs silently when the tunnel drops; a small follow-up
  task tracks adding a visible timeout and reconnect indicator.
