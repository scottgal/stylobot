# Soak plan — SQLite vs Postgres commercial persistence, head-to-head

Comparative load/soak: same commercial gateway image, same k6 load, **only the persistence backend
differs** (SQLite vs Postgres). Companion to `docs/soak-load-testing-runbook.md`.

## Why (the hypothesis)
resoak3/4 found the Postgres gateway pool-exhausts at ~100 RPS on the **read hot path** — per-request
`NpgsqlConnection` rentals in `GetFingerprintAsync` / `FingerprintApprovalStore.GetAsync` /
`GetTopBotsAsync` blow past Max Pool Size 100 → circuit opens. SQLite reads are **in-process** — there
is no client-server connection pool to exhaust. So the prediction is:

- **Postgres**: pool-exhaustion ceiling ~100 RPS (pre-GetTopBots-fix), rising after `44e20528` (GetTopBots
  now tick-fresh, no per-poll connection). New top holder should be `GetFingerprintAsync`.
- **SQLite**: no pool exhaustion at all; the ceiling (if any) is **write-side** — `SQLITE_BUSY` /
  "database is locked" under the write-behind drain, or single-writer serialization CPU. Reads should
  stay cheap.

The test either confirms the fix is a Postgres-read-path artifact (SQLite sails past 100 RPS) or reveals
a store-agnostic ceiling. Also validates **store-uniformity under load** (both backends fill + plateau,
no cross-store leakage).

## Setup — same image, backend as the only variable
- **Image:** the current staging commercial gateway `stylobot-gateway-enterprise` @ `44e20528` (FOSS
  `cdc86556` + commercial `e34951d`). Both persistence packs are compiled in (SKU=Enterprise).
- **Two runs, sequential, same instance** (NOT parallel — `.15` gateway is CPU-capped and parallel runs
  would contend and confound). Config-swap + recreate between runs. The host throws if both connection
  strings are set, so it's a clean either/or:
  - **Run P (Postgres):** `BotDetection__Commercial__Postgres__ConnectionString` = staging PG; Sqlite unset.
    Detection stores = commercial Postgres (`PostgreSQLFingerprintStore` etc.).
  - **Run S (SQLite):** `BotDetection__Commercial__Sqlite__ConnectionString` = a staging SQLite path;
    Postgres unset. Detection stores fall back to the FOSS SQLite stores; commercial tier = SQLite
    config-override. (Note the honest confound: this is "same gateway, different STORE IMPL," not
    "same store, different DB file" — that's the store-uniformity comparison we actually want.)
- **Fresh store each run** so we measure from a known baseline, not a warmed corpus:
  - Postgres: truncate the detection + fingerprint tables in the staging PG before Run P (the
    learned-state-reset table list, minus dashboard history if we want to keep it).
  - SQLite: wipe the gateway's `*.db*` files before Run S.
- **Keyed, poison-safe:** every request carries `X-SB-Api-Key: staging-test-website-key-do-not-use-elsewhere`
  (learning-suppressed — verified +1/60 this session). k6-plateau now injects it via `--env API_KEY=…`
  (FOSS `987bc415`). A **browser UA** on the human arm avoids the scraper-tarpit that inflates latency.

## Load — identical for both runs
```bash
cd ~/RiderProjects/stylobot
k6 run scripts/soak/k6-plateau.js \
  --env TARGET=http://192.168.0.15:8190 --env MAX_RPS=300 \
  --env API_KEY=staging-test-website-key-do-not-use-elsewhere \
  --summary-export /tmp/soak-<backend>-summary.json
```
Plateau ramp 10→20→50→100→150→200→300 RPS, 30s ramp + 90s hold per level, ~14 min. Record the start
epoch each run so onset can be mapped to the ramp level (the RPS where it first breaks).

## Measurements (per run)
Raw k6 signals only (`docs/soak-load-testing-runbook.md`):
- `http_req_waiting` p50/p95/max — server-side latency.
- `dropped_iterations` + `iterations` rate — sustained RPS achieved.
- `http_req_failed` rate.

Backend failure signal, from the gateway log (`docker logs stylobot-test-gateway --since 18m`):
- **Postgres:** `pool has been exhausted` count · circuit-breaker trips · `57014 canceling statement`
  timeouts · top connection-holding code paths (group the `NpgsqlConnection.Open` stacks).
- **SQLite:** `SQLITE_BUSY` / `database is locked` count · write-behind drain lag / dropped batches ·
  any `Cache=Shared` contention warnings.

Store behaviour + host:
- Table/`.db` growth → **plateau** (bounded, not unbounded flood): PG `count(*)` per store table;
  SQLite `.db` file sizes over time.
- `docker stats` CPU on the gateway (SQLite single-writer serialization may show as higher CPU).
- Poison check after each run: signature count delta stays ~flat (proves the key suppressed learning).

## Deliverable
One comparison table:

| | SQLite (Run S) | Postgres (Run P) |
|---|---|---|
| Server p95 (`http_req_waiting`) | | |
| Sustained RPS achieved | | |
| dropped_iterations | | |
| Onset RPS (first failure) | | |
| Failure mode | `SQLITE_BUSY`/locks? | `pool exhausted` count |
| Top holder / hot path | | GetFingerprint? |
| Store growth | plateau? | plateau? |
| Gateway CPU | | |

Plus a one-paragraph read: is the ~100 RPS ceiling Postgres-read-path-specific (SQLite passes it), did
`44e20528`'s GetTopBots fix raise the Postgres ceiling, and does store-uniformity hold under load on both.

## Hygiene / hard rules
- **SUPERSEDED 2026-07-30:** `.15:8190` is staging.stylobot.net's live gateway (shared corpus, real
  traffic), not an isolated rig — the poison-suppression key only protects the *learned model*, not
  *capacity*. Soaking it, even keyed, adds real load to staging. Do not target `.15:8190` from any soak
  run. Use a dedicated commercial gateway + fresh Postgres on their own ports instead
  (`run-backend-soak.sh` now hard-refuses any TARGET containing `:8190` or `staging.stylobot.net`).
- NEVER soak prod (poisons the clean corpus).
- Sequential, not parallel (shared 1-CPU gateway).
- Keyed on every request; recreate the gateway between runs; fresh store each run.
- `deploy-` owns the `.15` execution (config swap, recreate, docker log capture); k6 runs from the dev box.
