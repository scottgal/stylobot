# Economy mode (7.1+)

Economy mode is a zero-persistence, low-memory deployment shape for
constrained-resource environments. Enable it with the `-e` / `--economy`
flag on the `stylobot` CLI, or set `STYLOBOT_INMEMORY=1` directly:

```bash
stylobot 5080 http://localhost:3000 -e
```

Detection runs unchanged. Everything that would normally write behavioural
state to SQLite is swapped for a no-op or in-process implementation, so
nothing survives a restart and the smallest Kestrel profile is selected.

## When to use it

- **Pi-class hardware** -- 256-512MB RAM single-board computers in front of a
  small site or local service.
- **Scratch containers** -- ephemeral pods / Lambda-style cold starts where a
  persistent volume is overkill and you accept losing learning between runs.
- **Demo / sandbox sites** -- the `x.stylo.bot` live sample hosts use this so
  every restart presents a fresh dashboard.
- **Integration tests** -- faster setup than tempdir SQLite, no cleanup, no
  shared-cache contention between parallel test workers.

It is **not** the right mode for a public production site: the parts that
make StyloBot smarter over time all get switched off, so you give up the
detection-quality compounding you would otherwise get from a few days of
real traffic.

## What you lose

Everything that depends on persistent state is gone. From most to least
visible:

- **Verdict cache** -- every signature is judged from scratch on first hit
  after restart instead of short-circuiting on a cached score. Cold-start
  CPU cost is slightly higher.
- **Metastable identity layer** -- `Identity.Enabled` is forced off.
  Per-fingerprint centroids, weight vectors, drift detection, archetype
  calibration and AI-opinion writebacks all do nothing. Rotated identities
  are no longer collapsed onto a single fingerprint.
- **Adaptive learning** -- `IWeightStore` is no-op, so detector confidence
  always uses YAML-default weights. `ILearnedPatternStore` is no-op, so the
  learning system observes patterns but never persists them.
- **Entity resolution** -- `ISessionStore` is no-op. No merge/split/rewind,
  no cross-session velocity, no entity edges in the dashboard.
- **Session vectorisation** -- sessions are still built per-request and the
  in-process Markov chain still fires per-request signals, but no session
  rows are persisted, so the Sessions tab and inter-session anomaly
  detection are both blank.
- **Leiden clustering** -- the cluster graph still builds in memory during
  a process lifetime, but the cluster snapshot is not persisted; the
  Clusters tab rebuilds from zero on every restart.
- **License grace timer** -- restart always re-enters the grace window
  cleanly. Fine for FOSS where licensing is informational.
- **PinnedEndpoints** -- operator-configured endpoint overrides do not
  persist. Configure via YAML / appsettings instead.
- **FingerprintApproval flow** -- the enterprise approval surface is no-op.
- **Honeypot path lifecycle** -- the threat scorer's "scanner probing a
  path that used to be real" signal is unavailable, so every 4xx looks
  equally suspicious.
- **PoW challenges** stay functional within a process (backed by a
  `ConcurrentDictionary`) but evaporate on restart.

## What you keep

Detection itself is **unchanged**. Every request still passes through the
full detector pipeline; every contributor still fires:

- All four foundation contributors run as normal
  (`Signature`, `TransportProtocol`, `PiiQueryString`, `IdentityVector`).
- Every fast-path detector runs (`UserAgent`, `Header`, `Ip`, `Behavioral`,
  `ClientSide`, `Inconsistency`, `VersionAge`, `Heuristic`, `CacheBehavior`,
  `CookieBehavior`, `ResourceWaterfall`, `ReputationBias`, `AiScraper`,
  `Haxxor`, `CveProbe`, `PiiQueryString`, ...).
- Advanced fingerprinting still runs
  (`TlsFingerprint`, `TcpIpFingerprint`, `Http2Fingerprint`,
  `Http3Fingerprint`, `MultiLayerCorrelation`, `BehavioralWaveform`,
  `ResponseBehavior`, `StreamAbuse`).
- All action policies still work (`block`, `throttle-stealth`,
  `throttle-tools`, `throttle-status`, `challenge`, `redirect-honeypot`,
  `logonly`).
- The in-process LFU and bounded caches
  (`SignatureAggregateCache`, `BoundedCache`, `WriteBehindLfuStore` family,
  `RdnsCache`, `CidrCache`, ...) provide a within-process working set.
  They reset on restart but absorb hot data during the run.
- The bot signature catalog (`botdetection.db`, ~1MB of bot-pattern regexes
  and datacenter IP ranges) is still loaded -- this is content cache, not
  behavioural state, and removing it would degrade UA detection.

## What ends up on disk

| File | Size | What it holds | Notes |
|------|------|---------------|-------|
| `botdetection.db` | ~1MB | Bot pattern catalog, datacenter IP ranges, list-update metadata | Reference data. Re-fetched on update; not user behaviour. |
| `sessions.db` | ~20KB | Empty schemas from `CentroidSequenceStore` + `AssetHashStore` (concrete classes registered without an interface to swap) | 0 rows in either table; bootstrap only. |
| `stylobot-config/` | small | YAML/JSON config override directory | Operator-facing, not state. |

No `fingerprints.db`, `clusters.db`, `challenges.db`, `approvals.db`, or
`path-lifecycle.db`. No row inserts into `sessions.db`.

## Kestrel sizing

`-e` also selects the `economy` Kestrel profile:

| Knob | Value |
|------|-------|
| Worker / IOCP min threads | 25 / 25 |
| Max concurrent connections | 500 |
| Max upgraded connections (WS / SignalR) | 50 |
| Max request body size | 32 KB |
| Keep-alive timeout | 15 s |
| Request-headers timeout | 5 s |
| Slowloris min data rate | 200 B/s |
| HTTP/2 max streams per conn | 50 |
| HTTP/2 initial window size | 64 KB |

Compared to `balanced` (50 / 50 threads, 10,000 conn, 256 KB body) this
gives up an order of magnitude of concurrency in exchange for fitting in
a small RAM footprint. If your traffic shape needs more than 500 concurrent
connections, do not use economy mode -- pick `balanced` or `site` and
accept the persistence trade-off the SQLite-backed mode gives you back.

## Disabling identity layer manually

If you want most of economy mode but keep persistence elsewhere, set the
identity options directly instead of using `-e`:

```jsonc
{
  "BotDetection": {
    "Identity": { "Enabled": false }
  }
}
```

Identity-layer dormancy is what makes `NullFingerprintStore` safe to bind;
the absorption / drift / calibration hosted services check the same flag
and short-circuit at the top of their loop. Pure FOSS defaults Identity to
off anyway, so this is mostly relevant for commercial deployments that had
turned it on.
