# SBC Benchmark Harness — StyloBot on real single-board computers

Runs the ceiling ladder + soak on a Raspberry Pi / Orange Pi (and an x86
reference) and produces the per-device numbers that feed
`docs/architecture/performance-benchmarks.md` (commercial repo).

**Tier ladder (lowest first):**

| Tier | Device | Backend | Answers |
|---|---|---|---|
| 1 | Raspberry Pi | SQLite (default) | "Can I run this on a Pi?" — the floor |
| 2 | Orange Pi | SQLite | How much more headroom? |
| 3 | Orange Pi | PostgreSQL | Is the PG uplift worth it on-device? |
| 4 | x86 (Maxo) | PostgreSQL | Reference — what the "real server" does |

Per tier, both single-site and three-site (3 virtual hosts) are measured,
with SQLite and PostgreSQL modes on the same device where the ladder calls
for it (the harness supports any device×backend combo).

## What it measures (per device, per backend)

- **Max RPS** before p95 latency degrades past **500 ms** (ceiling ladder:
  fixed-RPS levels, 5 → 150 RPS by default, each a separate k6 run)
- **Memory** — gateway RSS (docker stats) at idle and under load, sampled
  every 5 s; host free memory + loadavg alongside
- **CPU** — gateway container CPU% under sustained load (sampled)
- **Disk I/O** — SQLite file growth (`/app/data` bytes) and PostgreSQL
  WAL/data growth, sampled every 5 s across the run
- **Cold start** — compose start → `/admin/alive` → first `/stylobot/traffic`
  render (device boot time captured via `uptime -s` marker)
- **Soak stability** — 30 min at 50% of the ceiling, in 6× 5-min k6 windows;
  flatness = per-window p95/errors/drops (no drift = flat)

## Traffic mix (realistic, not just /health)

- ~85% site page loads through the full detection pipeline (10 human UAs +
  5 bot UAs, 6 paths)
- ~15% dashboard traffic: `/stylobot/traffic` (SSR page) + SignalR negotiate
  + `/api/summary` + `/api/timeseries`
- **Default config** — the public `scottgal/stylobot-gateway:latest` image,
  baked-in detection defaults, no tuning
- **Poison-safe** — every request carries `X-SB-Api-Key: SB-BENCH`, the key
  is configured `ActionPolicyName=logonly`, so the synthetic flood does not
  train the learned model and does not tarpit/block (latency stays clean)
- Three-site mode rotates the `Host` header across `site1/2/3.test`; YARP
  routes each host to its own nginx stub upstream (the multi-site example
  pattern)

## One command (driver on .15 — per operator directive, never the Mac)

```bash
# prerequisites on the driver (.15/Maxo):
#   k6 (single static binary), jq, sshpass
#   Docker on the target device (already installed on the Pi 5)

./sbc-bench.sh pi-sqlite              # Tier 1: Pi + SQLite, single site
./sbc-bench.sh opi-sqlite             # Tier 2
./sbc-bench.sh opi-postgres           # Tier 3
./sbc-bench.sh x86-postgres           # Tier 4 (gateway + k6 both on .15)

# options
./sbc-bench.sh pi-sqlite --sites 3    # three-site (3 virtual hosts)
./sbc-bench.sh opi-sqlite --no-soak   # ceiling only, skip the 30-min soak
./sbc-bench.sh pi-sqlite --levels "5 10 20 30 50"   # custom ladder
```

Output lands in `results/<tier>-<sites>s-<timestamp>/`:

- `ceiling.csv` / `ceiling.txt` — per-level p95/med/err%/achieved/dropped,
  and the ceiling (first level where p95 ≥ 500 ms or errors ≥ 1%)
- `soak-flatness.csv` — per 5-min window p95/err%/achieved/dropped
- `metrics.csv` — 5-s samples: gateway CPU/RSS, host free, loadavg, DB bytes
- `cold-start.txt` — health + dashboard-ready deltas
- `summary.txt` — run-level CPU/RSS/disk-growth aggregates

The script prints the full per-tier table at the end — RPS, memory, CPU —
ready to paste into `performance-benchmarks.md`.

## Layout

```
k6-sbc.js                     one k6 level (ceiling or soak window), shared mix
sbc-agent.sh                  on-device: compose up, cold-start timing, sampler
sbc-bench.sh                  driver: tier ladder -> soak -> collect -> table
docker-compose.sbc.yml        gateway + nginx stub upstreams (SITES=1|3)
docker-compose.sbc.postgres.yml   timescaledb overlay (backend=postgres)
config/site-1/                single-site YARP + appsettings (default config)
config/site-3/                three-site YARP (3 hosts) + appsettings
```

## Device registry / credentials

| Device | Host | User | Notes |
|---|---|---|---|
| Raspberry Pi 5 (8 GB, Ubuntu 24.04) | `192.168.0.39` | `claude` | reachable, Docker ready |
| Orange Pi (Ubuntu) | hostname `ubuntu` | `ubuntu` | operator powers on + provides IP |
| x86 reference | `localhost` (.15) | — | Tier 4 |

Override via env: `PI5_HOST`, `PI5_USER`, `PI5_PASS`, `OPI_HOST`, `OPI_USER`,
`OPI_PASS`, `K6_TARGET` (point k6 at a remote load-gen box for Tier 4),
`K6`, `JQ`, `SSHPASS`.

## Hygiene

- **Never run from the Mac** — the driver is .15 (operator directive); k6 on
  the driver, never on the target device (load-gen would contend for CPU and
  confound the numbers).
- **Never target staging/prod** — the harness stands up its own stack on the
  device; the device's own docker daemon is the only thing touched.
- Sequential runs only; each tier run recreates its own stack (`up -d` on
  the same device serializes naturally).
