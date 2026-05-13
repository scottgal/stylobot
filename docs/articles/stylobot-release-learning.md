# StyloBot Release Series: Learning to Get Faster

*How StyloBot's four-tier learning system turns repeat traffic into sub-millisecond decisions while keeping the door open for false-positive recovery.*

---

## The thesis: detection should get cheaper the more you see

A bot detector is asked the same question millions of times a day: *is this fingerprint a bot?* For the vast majority of those requests, the system already has an opinion. It saw the same TLS fingerprint, the same header order, the same User-Agent rotation pattern, the same IP /24 ten minutes ago, and it concluded `bot, 0.93`. There is no honest reason to spend another full detector pipeline on the same actor unless something about that actor has visibly changed.

StyloBot's learning system is built around that thesis. The pipeline runs in full when it has to. When it doesn't, the previous verdict is reused under controlled conditions, the request is served in microseconds, and the act of serving it still feeds the long-running memory so detection stays honest.

This article walks through how that loop is constructed: the four tiers of learning, the cache that hangs off them, and the design choices that protect the system from being wrong about a fingerprint and refusing to change its mind.

## Four tiers of memory

Learning in StyloBot is layered so each tier corresponds to a different lifetime:

1. **Fast-path reputation (instant).** A short list of patterns that have been classified as confirmed-bad. A request matching one of these aborts the pipeline at priority 3, before any other detector runs. This list is small, conservative, and only entered once a pattern reaches a high score with a large support count.
2. **Intra-request blackboard (per-request, milliseconds).** Detectors write signals to a shared signal sink during the request. Later detectors read those signals. This isn't long-term memory; it's how the 49 detectors coordinate within one request without each having to recompute features the others already extracted.
3. **Inter-request reputation (minutes to days).** Pattern-level memory: the `PatternReputation` store tracks per-pattern bot scores with online EWMA updates and time decay. Each pattern has a state (`Neutral` → `Suspect` → `ConfirmedBad`) with asymmetric promotion and demotion thresholds.
4. **Per-fingerprint verdict cache (minutes).** Per-actor memory: the live sliding window in `SignatureCoordinator` carries each observed fingerprint's running posterior, sample count, last-seen time, latest risk band, and latest threat score. This is the layer the new verdict gate reads from.

Tier 1 short-circuits the pipeline. Tier 2 coordinates within it. Tier 3 is the long memory that survives across days. Tier 4 is the short memory that lets repeat traffic skip the pipeline entirely.

## EWMA as the shape of forgetting

Every numeric memory in the system uses the same shape of update:

```
new = (1 - α) × previous + α × observation
```

Pattern reputation, per-detector weights, per-signature `bot_probability`, the pipeline load sensor's smoothed RPS — all of them blend a new observation into a running value with a small mixing weight `α`. The default is 0.15 for the signature posterior, 0.10 for pattern reputation. The mathematical effect is that any single observation contributes only a fraction; it takes a sustained pattern of observations of one kind to move the running value substantially.

This shape was deliberately chosen over the obvious-but-wrong alternative: storing the *maximum* probability ever observed. A max-of-history store would let a single 0.95 spike pin a fingerprint at 0.95 forever, no matter how it behaved afterwards. The EWMA store has the opposite property: a 0.95 spike followed by hundreds of benign observations decays smoothly back toward benign. False positives are recoverable.

The decay extends to the patterns themselves. Pattern reputation has separate time constants for score (τ = 7 days) and support (τ_support = 14 days). A pattern that hasn't been seen in three weeks has effectively zero influence on detection; one that hasn't been seen in three months is garbage-collected entirely. Memory that doesn't decay is memory that drifts away from reality.

Hysteresis is built into the state machine itself. Promoting a pattern to `Suspect` requires score ≥ 0.6 and support ≥ 10. Promoting to `ConfirmedBad` requires score ≥ 0.9 and support ≥ 50. Demoting *back* from `ConfirmedBad` requires score ≤ 0.7 and support ≥ 100. It is deliberately harder to forgive than to accuse, but forgiveness is always possible without an admin override.

## The verdict cache: earned scaling

The per-fingerprint sliding window is the layer the request hot path now consults directly. On each request, before the orchestrator runs, `SignatureVerdictGate.DecideAsync` looks up the requesting signature and chooses one of four actions:

- **Miss.** No usable record for this fingerprint, or one older than the policy's `BiasMaxAgeSeconds`. The full detector pipeline runs. The result feeds the sliding window for next time.
- **Bias.** A record exists with moderate confidence, or it's slightly stale. The pipeline runs, but the cached verdict is injected as a Wave-0 prior contribution. The posterior is pulled toward the prior in proportion to prior confidence and a linear age decay, so a confident recent prior anchors the answer while a low-confidence or stale prior barely touches it.
- **Skip.** The record is recent and confident enough that the pipeline contribution would be marginal. The cached verdict is enforced; the orchestrator is skipped. The request is served in microseconds.
- **Watchdog-trip.** Skip-eligible cache hit, but the variance watchdog detected something that means "this fingerprint is now doing something atypical for this fingerprint." The cached verdict is invalidated for this request; the pipeline runs fresh.

Skip is the path that gives the thesis its teeth. Once a fingerprint has been seen with enough confidence in either direction (this is important: the gate is direction-agnostic, so sure-human and sure-bot are equally eligible to skip), every subsequent request from that fingerprint costs the gate lookup, an observation record, and the policy enforcement. On a modern CPU that's a few hundred microseconds.

A sustained run of requests from a known-good fingerprint will see the first request at ~50ms and every subsequent request at ~300µs until the freshness window closes or the watchdog trips. The Skip path is what makes the system gracefully cheap.

## How false positives don't compound

Caching a verdict is a bet that the fingerprint is going to keep behaving the way it just behaved. The bet is wrong sometimes. The design assumes it is wrong and works to keep the wrongness bounded.

**Direction-agnostic confidence.** A sure-bot verdict and a sure-human verdict both qualify for Skip. This sounds obvious but it's the difference between "we cache bot verdicts" (a system biased toward false positives) and "we cache *confident* verdicts" (a system biased toward whichever way the evidence pointed). High-confidence human traffic gets the same speedup as high-confidence bot traffic.

**EWMA, not MAX.** A single high-probability observation does not pin the cached verdict. It contributes 15% to the running value; the next 85% comes from whatever else this fingerprint did. A genuine attacker accumulates evidence quickly. A legitimate visitor who happened to look like a scraper for one request decays back toward benign on subsequent observations.

**Sample-size gating.** The cached verdict's confidence grows with sample count and is fully realised only at ~10 observations. Below that, the gate prefers Bias (pipeline runs, prior used as a hint) over Skip. The decision to fully trust the cache is held back until the sample is large enough that the EWMA is meaningful.

**Variance watchdog.** Even with a confident, fresh cached verdict, Skip can be vetoed per request. The watchdog tracks three properties per fingerprint:

- *IP rotation:* if the same fingerprint suddenly appears from a new /24 within the rotation window (default 300s), the cached verdict is suspect.
- *Rate spike:* if the request rate over the last minute exceeds 10× the rolling five-minute baseline, the cached verdict is suspect.
- *Path divergence:* after a fingerprint has visibly settled into 3+ path families, a never-before-seen path family for this fingerprint is suspect.

Any one of these trips, and the pipeline runs fresh. The cache becomes a hint again instead of a decision.

**Skip-path observation.** Skip requests still record an observation into the sliding window. The fact that detection was skipped doesn't create a hole in the per-fingerprint history; clustering, drift detection, and the dashboard's per-fingerprint stats see every request whether or not the pipeline ran. The window stays current so the next decision is made against a current picture.

**Refresh sampling.** A configurable fraction of Skip-eligible requests (default 5%) is deterministically downgraded to Bias so the pipeline runs and refreshes the live record. The signature hash decides which requests get refreshed, so retries land identically, but over time every fingerprint gets a periodic full re-evaluation. The cache cannot drift far from reality between rotations.

**Entity-family fallback.** When a fingerprint rotates and its new identity has no cached verdict of its own, the gate falls through to the family's canonical signature. A bot that's been merged into a family because the behavioral vector matched a known sibling inherits its sibling's verdict instead of starting from scratch — but only if the family anchor is itself still in the sliding window. Cold family anchors evict naturally; split events drop the family mapping. There is no separate invalidation channel because the sliding window's TTL is the invalidation channel.

## The verdict cache as a contribution, not a decision

A subtle but important property of the Bias path: the cached verdict is injected as a Wave-0 *contribution*, not as a final answer. The `FingerprintPriorContributor` emits a single calibrated contribution whose effective weight is:

```
prior_confidence × multiplier × linear_age_decay
```

A 30-second-old verdict with confidence 0.9 anchors the posterior strongly. A 23-hour-old verdict with confidence 0.4 barely touches it. A 26-hour-old verdict has zero effective weight even if it would have qualified for Skip on its own.

The downstream aggregator computes a `RequestContributionDelta` showing how much this specific request moved the fingerprint's score. When the dashboard renders the detection feed, it shows that delta instead of the per-request absolute probability. A row no longer reads "this request scored 0.42 bot" — which is misleading on cached verdicts where the per-request evidence is light. It reads "this request moved the fingerprint score by +0.5%", which is the question someone investigating the feed actually wants to answer.

## What this looks like at runtime

Once warmed, the live pipeline distribution settles into a pattern like:

| Path | Fraction of traffic | Typical latency |
|---|---|---|
| Skip | majority for sustained traffic | sub-millisecond |
| Bias | new + recently-warmed fingerprints | full pipeline + prior |
| Miss | brand-new fingerprints, rare rotations | full pipeline |
| Watchdog-trip | something changed | full pipeline + reason |

The CLI dashboard shows this directly. Cached rows carry a dim asterisk in front of the timestamp. The Top Fingerprints sidebar shows each known fingerprint's EWMA-smoothed posterior with an 8-sample sparkline of recent observations, so volatility shows up as a trend rather than as a row-by-row swing in the feed. The bullet next to the fingerprint reflects the EWMA — the stable verdict — not whichever way the most recent request happened to swing.

That visual choice matches the system's behavior. The per-request score is information about the request, not about the actor. The actor's score moves slowly and on purpose.

## Why this is the shape

A bot detector that runs the full pipeline on every request is honest but uneconomic. A bot detector that caches verdicts without recourse is fast but brittle. The design StyloBot has converged on treats the cache as the common case and the pipeline as the recovery path: Skip is what happens when there is nothing new to learn, Bias is what happens when the pipeline should run but the prior is still informative, Miss is the cold-start, and the watchdog is the safety net for when the cache is wrong.

The EWMA shape of every memory, the asymmetric hysteresis in the state machine, the sample-size gate on confidence, the deterministic refresh sampling, the path-family memory, and the family-canonical fallback all exist for the same reason. Each one is a place the system can be wrong about a fingerprint and still recover, on its own, without an operator intervening.

The headline number is the latency — repeat traffic in microseconds — but the actual feature is the recovery posture. Detection that learns is only useful if it can also un-learn.
