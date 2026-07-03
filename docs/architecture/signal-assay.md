# Signal Assay — environmental signal adaptation

## The failure this fixes

Behind a Cloudflare tunnel (and every analogous L7-proxy / TLS-terminating
topology) the origin receives a plain HTTP/1.1 hop from the local `cloudflared`
connector. The entire transport tier — TLS/JA3, TCP/IP fingerprint, raw HTTP/2
and HTTP/3 frame data — **never reaches us**. Today the pipeline conflates two
very different absences:

- **absent because the environment cannot provide it** (tunnel strips transport)
- **absent because a bot omitted it** (a scraper that doesn't speak the protocol)

and scores both as bot evidence. Concrete regression (staging, 2026-07-01): a
correctly-identified **Chrome 126 on Windows 10** was scored `risk=0.790
VeryHigh / threat Critical` and served a **403**, taking `staging.stylo.bot`
down. It regressed the moment the k8s PROXY-protocol / source-IP-preservation
work (task #31) shipped in the gateway image — that work made the transport tier
load-bearing, which is correct for direct-to-origin k8s but wrong for the
simpler tunnel case where the tier legitimately does not exist.

The ungated penalty sites the map found:

| Site | Behaviour | Weight |
|---|---|---|
| `TcpIpFingerprintContributor:258-276` | missing `Connection` header → bot | Δ0.2 × 0.7, **ungated by trust** |
| `TlsFingerprintContributor:159-169` | `!isHttps` and no `X-Forwarded-Proto` → bot | Δ0.05 × 0.3 |
| `Http2FingerprintContributor:304-320` | missing stream-priority → bot | Δ0.1 × 0.6 (gated by trust) |
| `TransportHeaderTrust` Auto mode | Cloudflare peer IP is *public* → `trustHeaders=false` → **every** `X-*` transport header discarded | — |

## Principle

> A signal the environment **structurally cannot provide** is scored
> **UNAVAILABLE (neutral)**, never as bot evidence. Whether a missing signal is
> "unavailable" or "suspicious" depends on whether *this deployment's
> environment is expected to deliver it* — and that expectation is **learned per
> deployment**, not configured per request.

This is the environmental-adaptability idea: the gateway assays its own
environment on startup, discovers which signal tiers are live, and adapts its
scoring so it is neither blind nor paranoid.

### Security invariant (why "ignore missing" is not an evasion hole)

The same missing signal is read differently by context:

- absent transport **from the assayed/trusted upstream** (the tunnel connector)
  → **expected → neutral**
- absent transport **on a direct-to-origin connection** → **anomalous → still
  suspicious**, full transport scrutiny applies

The relaxation is gated on the connection arriving from the established trusted
upstream. An attacker who reaches the origin directly and strips transport data
does **not** inherit the tunnel's easier ride. This preserves the "we cannot
hide any traffic / cannot open a security hole" rule: nothing is excluded, the
*interpretation* of an absence is environment-aware.

## Model — an availability dimension on the signal surface

`signal-contracts.md` establishes: one fact, one canonical writer, merged into
`AggregatedEvidence.Signals`, asserted by the BDF rig. Signal Assay adds an
orthogonal **availability** dimension to each transport-tier signal:

- `Available(value)` — the environment delivered it; score on the value
- `Unavailable(reason)` — the environment does not deliver it here; contribute
  nothing (neutral), and record *why* (topology)
- (unchanged) `Present-but-negative` — delivered and it looks bot-like

Distinguishing **missing** from **negative** is the crux the current code lacks.
The availability verdict per tier is a foundation fact (established before
classifiers weigh in), sourced from the **Signal Profile**.

### The Signal Profile

A per-deployment (per-site, see `Sites:Domains`) map:

```
tier -> { availability, confidence, samples, firstSeen, lastSeen, reason }
```

Tiers: `Tls`, `TcpIp`, `Http2`, `Http3`, `TransportProtocol`, `UserAgent`,
`Header`, `Behavioral`, `Ip`, `Reputation`, `FingerprintPrior`.

Persisted durably (Postgres commercial / SQLite FOSS — **never** `IMemoryCache`
as the store of record; a hot read-through cache in front is fine). Keyed by
site so a multi-tenant gateway can serve a direct-origin tenant and a
tunnelled tenant with different profiles simultaneously.

## Warm-up assay

Runs over the first **N** requests (or **T** duration) after startup and
whenever a re-assay triggers. **Not a `BackgroundService`** — driven by the
`ScheduleCoordinator` tick + a foundation observer that accumulates per-request
tier-presence into the running assay.

- Observe, per request, which tier signal keys appeared in `evidence.Signals`.
- A tier consistently **absent across the window**, while otherwise-complete
  requests flow, → `Unavailable`. A tier that appears in ≥ `MinSamplesPerTier`
  → `Available`.
- On window completion: publish the Signal Profile (foundation fact) + persist.
- **Re-assay trigger:** a tier flips (an `Unavailable` tier starts appearing, or
  an `Available` tier vanishes for `ReassayFlipWindow`) → mark the profile
  `Reassaying` and re-open the window. Topology changed under us.

While `WarmingUp`, scoring runs in a **conservative** mode: absent transport is
treated as `Unavailable` provisionally (fail toward *not blocking* real
browsers) until the profile establishes. Rationale: a cold start behind a tunnel
must not 403 the first N legitimate visitors.

## Adaptive scoring

Transport contributors consult the profile:

- tier `Unavailable` → contributor returns `Unavailable` (zero weight, neutral),
  with a signal recording the reason. The three ungated penalties above become
  conditional on the tier being `Available` in the profile.
- tier `Available` → unchanged behaviour (score on the value).

**Sensitivity ceiling.** When high-discrimination tiers are `Unavailable`, the
achievable bot-probability/confidence is **capped**: you cannot be
VeryHigh-confident a well-formed browser is a bot on UA + headers + behavioural
alone. The cap is the quantitative form of "reduced sensitivity" and is what
prevents a 0.79 block when the transport tier is dark. Cap curve is
configurable.

## Real client IP + trusted upstream

- Resolve the real client IP from `CF-Connecting-IP` / `X-Forwarded-For`, gated
  on the connection arriving from the trusted upstream (ASP.NET
  `ForwardedHeaders` + `KnownProxies`/`KnownNetworks`).
- The assay **auto-proposes** the trusted upstream (the consistent immediate
  peer address seen during warm-up); the operator confirms/pins it via config.
  Auto-propose is a convenience; the *trust* is an explicit pin (never trust
  forwarded headers from an unpinned peer — security).

## Sensitivity score + dashboard surface

`sensitivity = f(live tiers weighted by discriminative power)`, tier weights
configurable. Surface copy:

> **Running at 68% sensitivity** — TLS/JA3, TCP/IP and HTTP/2 fingerprinting
> unavailable (Cloudflare tunnel detected).

**Dashboard "Signal Assay" panel** (SSR-first, SignalR invalidation beacon per
the dashboard pattern; real data, no demoware):

- each tier: `Available` / `Unavailable` + reason
- detected topology label (e.g. *Cloudflare tunnel*, *direct origin*, *behind
  L7 proxy*)
- the sensitivity banner
- assay status: `WarmingUp (n/N)` / `Established` / `Reassaying`

This is the natural home for the **"Topology Issue"** hint (task #37): an
unexpected profile (e.g. transport tier flipping intermittently → a
mis-configured proxy) raises the hint from the same data.

## Configurable settings

All on an options class (`SignalAssayOptions`), section
`BotDetection:SignalAssay`:

| Setting | Default | Meaning |
|---|---|---|
| `WarmupRequests` | 200 | requests in the assay window |
| `WarmupDuration` | 10m | wall-clock cap on the window |
| `MinSamplesPerTier` | 20 | samples before a tier is called `Available` |
| `ReassayFlipWindow` | 50 | consecutive contradicting samples to re-open |
| `WarmupConservative` | true | fail-open on absent transport while warming |
| `SensitivityWeights` | per-tier map | discriminative weight per tier |
| `SensitivityFloor` | 0.35 | never drop below this even fully dark |
| `TrustedUpstreams` | [] | pinned proxy peers (CF connector) |
| `TierOverrides` | {} | force a tier `Available`/`Unavailable` (escape hatch) |

## Relationship to existing tasks

- **Implements task #35** (missing TLS/JA3 must not default to bot) — this is
  the general solution.
- **Feeds task #37** (Topology Issue self-detection hint) — same assay data.
- **Aligns with task #39** (config = priors) — the profile is a *learned*
  prior, discovered not declared; config only pins trust + tunes.

## Test plan

- BDF rig: a **tunnel-topology replay** (transport headers stripped, forwarded
  from a pinned trusted upstream) must score a real Chrome as **human**.
- A **direct-origin transport-stripped** replay must remain **suspicious**
  (security invariant).
- Assert the availability facts flow into `evidence.Signals` under
  `DetectionPolicy.Default` (not Demo), per signal-contracts Rule 4.
- Sensitivity cap: a synthetic max-bot-on-UA-only request behind a dark
  transport tier must not exceed the configured ceiling.

## Build order

1. **Environment-adaptation core** (unblocks staging): availability-aware
   transport contributors + trusted-upstream client-IP + provisional
   conservative mode. This alone restores correct scoring behind the tunnel.
2. **Assay + Signal Profile** persistence + re-assay.
3. **Sensitivity score + Signal Assay dashboard panel.**

Nothing is dropped; this is landing order. Step 1 is both the outage fix and the
foundation the rest reads from.