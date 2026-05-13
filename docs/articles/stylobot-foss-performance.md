# StyloBot FOSS Performance Characterisation

*Reproducible perf measurements against `Mostlylucid.BotDetection.Demo` with the `full-demo` policy. Each section names the harness so the same numbers can be regenerated on another machine.*

---

## Test bench A — Apple M5 (10C, 32GB)

- **Host:** macOS, Apple M5, 10 cores, 32GB RAM
- **Build:** `dotnet run --project src/Mostlylucid.BotDetection.Demo -c Release`
- **Policy:** `full-demo` — 19 fast-path detectors, no LLM, no slow-path. The same setup any FOSS user gets out of the box.
- **Backing store:** SQLite (file-local, WAL mode)
- **Loopback only.** No TLS termination, no upstream service, no network stack in the path.

This is a small-to-mid-range development box. Headroom on the 9950X test bench (Section "Test bench B" — to be filled) will be substantially higher.

## Throughput: k6 ramp 1→60 VUs over 90s

**Harness:** `scripts/k6/k6-detection-throughput.js`

```bash
k6 run scripts/k6/k6-detection-throughput.js
```

The script ramps virtual users 1 → 10 → 30 → 60 → 0 over 90 seconds with rotating UAs, IPs, and paths, so each request looks like a fresh fingerprint. This is the **Miss-dominated** profile: every request runs the full detector pipeline.

| Metric | Value |
|---|---|
| Sustained throughput | 491.6 req/s |
| Total requests | 44,250 in 90s |
| Detection p50 | 50.0 ms |
| Detection p95 | 52.0 ms |
| Detection p99 | 134.0 ms |
| Bot classification | 100.0% (k6 clients look like bots — no browser TLS/headers) |
| Error rate | < 5% (some timeouts at 60 VUs peak) |

Latency above ~50ms is dominated by request queueing on the M5's 10 logical cores under 60 concurrent VUs, not by detection cost itself. The next section measures the detector pipeline directly.

## Per-request detection cost (no contention)

**Method:** sequential `curl` requests to a single warmed fingerprint, reading the `X-Bot-Processing-Ms` response header.

```bash
UA="Mozilla/5.0 (Macintosh; ...) Safari/605.1.15"
for i in {1..15}; do
  curl -sI -A "$UA" http://localhost:5080/test-path-fp-1 \
    | grep -iE "Risk-Score|Processing-Ms" | tr -d '\r'
done
```

| Request | Risk-Score | Processing-Ms |
|---|---|---|
| 1 (cold) | 0.779 | 0.9 |
| 2 | 0.856 | 0.4 |
| 3 | 0.896 | 0.9 |
| 4 | 0.896 | 0.4 |
| 5 | 0.896 | 0.7 |
| 6 | 0.900 | 0.6 |
| 7–15 (settled) | 0.900 | 0.4 – 0.6 |

Even at request 1, the full pipeline costs under 1ms. The risk score climbs through the EWMA-smoothed bot probability and plateaus at 0.900 by request 6 (the `NonAiMaxProbability` ceiling that prevents non-AI verdicts from exceeding 0.90).

The 50ms `http_req_duration` p50 from the k6 ramp is therefore **not** detector cost — it's request queueing under concurrency. A single client gets sub-millisecond detection from the same pipeline.

## Memory footprint

**Method:** `/usr/bin/vmmap --summary <pid>` after the k6 throughput ramp completed.

| Metric | Value |
|---|---|
| Virtual size | 822.8 MB |
| **Resident (RSS)** | **108.4 MB** |
| Dirty | 416 KB |
| Swapped | 1344 KB |
| Region count | 298 |

108 MB resident for a .NET app holding 49 detectors, the blackboard orchestrator, the sliding-window signature coordinator (1000-entry capacity), SQLite WAL connections, and a YARP proxy stack is well within the envelope for a Pi4-class deployment.

## Reproducing on another host

The k6 throughput script is the canonical FOSS perf benchmark. To regenerate the numbers on a different box:

```bash
# Required: dotnet 10, k6 ≥ v1.7
git clone https://github.com/scottgal/stylobot && cd stylobot
rm -f src/Mostlylucid.BotDetection.Demo/sessions.db*  # fresh signature window
ASPNETCORE_URLS="http://localhost:5080" \
  dotnet run --project src/Mostlylucid.BotDetection.Demo -c Release &

# Wait for "Now listening on: http://localhost:5080" in the log.
k6 run scripts/k6/k6-detection-throughput.js
```

The summary panel printed by the script (`Detection Throughput Benchmark Results`) is the line to compare across hosts.

Memory: `vmmap --summary $(pgrep -f BotDetection.Demo)` on macOS, `cat /proc/$(pgrep -f BotDetection.Demo)/status | grep -E 'VmRSS|VmSize'` on Linux.

## Caveat: verdict-cache engagement

The `X-StyloBot-VerdictSource` header was not observed on this run with the `full-demo` policy. The cache substrate is wired (the `SignatureCoordinator` is initialised in the demo's startup log: `window=00:15:00, maxSignatures=1000, ttl=00:30:00`) but the gate's Skip path did not engage under either k6 throughput or repeat-fingerprint loops. This is a follow-up to investigate; the FOSS perf numbers above reflect the **pipeline-on-every-request** path, which is the conservative upper bound on cost. Cache engagement can only make these numbers better.

## Test bench B — Ryzen 9 9950X (16C/32T, 96GB) — TODO

To be measured on `192.168.0.15` (scott / stylobot2026). Same harness, same demo build, same policy. Expected differences vs. the M5:

- ~3× higher sustained throughput from doubled core count + higher base clock
- Lower p99 tail under 60 VU peak (more headroom)
- Similar per-request detection cost (~ sub-ms) — single-thread perf is comparable
- Memory baseline unchanged (~ 100MB RSS); growth bounded by sliding-window LRU at 1000 signatures

The Test bench B numbers will replace this section once captured.

## Results format

For each future bench, fill in:

| Section | Notes |
|---|---|
| Hardware | CPU model, cores, RAM |
| Demo invocation | Exact `dotnet run` command + env vars |
| Policy | `full-demo` (or other) |
| Throughput | Sustained RPS, total requests, p50/p95/p99 latency |
| Per-request | Cold-to-warm latency trajectory for a single fingerprint |
| Memory | Resident set size after k6 ramp |
| Caveats | Anything unusual (cache state, IO contention, etc.) |

Same harness, same shape of result. No two benches need to invent their own metric.
