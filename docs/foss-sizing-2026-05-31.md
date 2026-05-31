# FOSS SQLite gateway — sizing recommendations

Empirical results from the 2026-05-31 stepped ceiling soak on the standalone
AOT gateway (win-x64, `balanced` Kestrel profile, detection ON via API
key, in-process Kestrel upstream stub). Same `stylobot` Console binary
the GitHub Release ships.

50-min ladder: 50 → 100 → 200 → 400 → 800 RPS in 10-min plateaus.
**246,403 requests served, 99.3 % success, 660 k k6 iterations dropped at
the upper plateaus because the gateway refused the excess at TCP.**

## What the gateway actually does at each load

Effective throughput is what `sessions.db` and successful responses tell
us, not what k6 *tried* to send.

| Target RPS | Effective served | Memory (steady plateau) | Sessions.db growth | Verdict |
|---|---|---|---|---|
| 50 RPS | ~50 RPS | **195–260 MB** | 1.0 MB / min | Comfortable. Detection is the dominant cost, not Kestrel. |
| 100 RPS | ~100 RPS | **320–590 MB** | 2.0 MB / min | Working hard but stable. RSS rises with thread-pool growth, GC keeps it in band. |
| 200 RPS | **~100 RPS (capped)** | 660–735 MB | 1.7 MB / min | Cap. Kestrel refuses the excess; thread pool stops growing at 807. |
| 400 RPS | ~100 RPS (capped) | 700–755 MB | 1.7 MB / min | Same as 200; gateway "boring under load". |
| 800 RPS | ~100 RPS (capped) | 700–785 MB | 1.8 MB / min | Identical to 400. 16× overload absorbed without degradation. |

**The interesting line is the wall between 100 and 200 RPS.** Below it,
every offered request gets a verdict + proxy round-trip. Above it,
Kestrel + the ThreadPool reach equilibrium at ~807 in-flight handlers
and any further offered load is dropped at TCP (RST). No queue runaway,
no memory blowout, no crash — exactly what the explicit Kestrel limits
in `8023555e` were designed to do.

This ceiling is **per-process** for a single FOSS+SQLite gateway with
the default `balanced` Kestrel profile. Higher numbers are possible:
horizontally with multiple gateway processes behind a load balancer, or
vertically with the `site` profile (200/200 thread-pool min) for a
public-site shape, or commercially by swapping the SQLite sessions store
for the Postgres one.

## Sizing by deployment target

The "you can run stylobot on …" table. RAM headroom = ~1.5 × the
observed steady-state plateau so a memory spike doesn't OOM. CPU
estimates assume two cores busy at peak (Kestrel + detection pipeline +
session writer). Storage budget = 4 hours of `sessions.db` growth +
1 GB working dir; everything else is essentially flat.

| Use case | Target RPS | Recommended box | Why |
|---|---|---|---|
| Personal site / blog | up to **20 RPS** | **Raspberry Pi 4 (2 GB)**, t3.nano EC2, smallest DO/Hetzner droplet | RSS will sit around 150 MB; 200 MB sessions.db over a week. CPU is barely awake. |
| Small business site, dev/staging | **20–50 RPS** | **Pi 4 (4 GB)**, t3.micro, $5–10/mo VPS | Headroom: 200–260 MB plateau + GC slack. Fits comfortably in 1 GB OS, leaves 3 GB for everything else. |
| Mid-traffic public site, REST API | **50–100 RPS** | **2 vCPU / 2 GB VPS** (Hetzner CX22, DO 2 GB, t3.small) | RSS plateaus at 500–600 MB at 100 RPS. 2 GB box leaves 1 GB for the kernel + page cache. Storage: ~120 MB/hour of sessions.db -- monitor disk weekly. |
| **At the per-process ceiling** (100+ RPS sustained) | ~100 RPS per gateway | **4 vCPU / 4 GB VPS** OR **2× 2 vCPU gateways behind a LB** | Single gateway: 700–800 MB plateau, two cores busy. Two gateways: each at 50 RPS comfortably -- much more headroom. |
| Above 100 RPS sustained | 200+ RPS | Multiple gateways OR commercial PostgreSQL upgrade | Single-process SQLite gateway is hard-capped here. Either scale horizontally (each gateway has its own sessions.db; signatures aren't shared) or move to the commercial Postgres path which removes the per-process session-write bottleneck. |

## What the ceiling actually is

A few of the cap candidates, ranked by likelihood:

1. **Kestrel.MaxConcurrentConnections = 10 000 isn't the wall** — we
   saw 807 in-flight handler threads, plenty of head-room left. It's
   the *handler-thread saturation* that ratelimits, not the connection
   table.
2. **ThreadPool equilibrium at 807 threads** — Default
   `SetMinThreads(50, 50)` (balanced profile) lets the pool grow as
   needed, but the grow-rate is bounded (~1 thread / 0.5 s under
   pressure). Effective ceiling = `handlers_active_at_equilibrium ÷
   p50_latency`. ~807 ÷ 8 s ≈ 100 RPS — matches what we see.
3. **SQLite single-writer per DB** — `sessions.db` accepts one writer
   thread; everything else queues. At ~100 RPS the queue stays drained;
   at 200+ RPS writes start backing up, which is why the gateway
   chooses to refuse new connections rather than queue them.

## To go higher

- **Profile up.** `STYLOBOT_PROFILE=site` raises `SetMinThreads(200, 200)`
  and `MaxConcurrentUpgradedConnections=10 000` — pre-warmed thread pool
  cuts the time to ramp under burst. Expected new ceiling: ~200 RPS
  per process.
- **Horizontal scale.** Run 2 or 4 gateways behind nginx / Caddy /
  Cloudflare. Each gateway is independent (own sessions.db). Signatures
  are gateway-local in FOSS, so a returning visitor may land on a
  different gateway and pay the cold-start price again -- acceptable
  for most traffic shapes.
- **Commercial Postgres.** Replaces the SQLite single-writer bottleneck
  with a connection pool to a shared Postgres. The interface refactors
  shipped in 7.0.0 (`IFingerprintStore`, `IClusterStore`,
  `ILicenseGraceStore`, `Func<DbConnection>` for `AssetHashStore` /
  `CentroidSequenceStore`) are what let the commercial layer swap the
  whole persistence path without re-implementing detection.
- **Run on Linux instead of Windows.** This soak was on a Windows AOT
  box. The Console binary's Linux build typically shows lower per-thread
  stack overhead (~1 MB vs ~2 MB on Win) and shorter wake-up latency on
  IOCP / epoll — both push the per-process ceiling up by ~20–40 %.

## What this does NOT measure

- LLM escalation path. The soak ran with API-key bypass; no Ollama /
  cloud LLM hit. If you turn LLM on, expect 100-300 ms p99 added per
  detection escalation. LLM detection is rate-limited internally, but
  it does add memory pressure.
- Real-internet RTT. Soak ran over LAN-WiFi (4-5 ms RTT). Internet
  clients add 30-100 ms per hop, but that's an *additive* cost on top
  of the gateway's 11 ms p50 — not a throughput multiplier.
- Sustained week-long behaviour. 50-minute soak validates "won't crash
  in 50 min under abuse". Longer runs would surface slow leaks (none
  observed in the 1-hour soak earlier today, but a week is different).
- Reverse-proxy fronting (Caddy / nginx). The fronting proxy adds its
  own connection pool that can absorb some of the burst the gateway
  refuses, so a real deployment behind nginx will smooth the
  failure-mode visible here.
