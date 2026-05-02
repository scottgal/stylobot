# Load Test Suite Design

**Date:** 2026-05-02  
**Target:** StyloBot FOSS, local M5 Mac Air, with progressive phases to multi-machine

## Goal

Find the RPS ceiling and latency profile of the full detection pipeline across four progressive modes:
log-only baseline, LLM-in-the-path, DDoS/edge flood, and distributed multi-machine (including Raspberry Pi as attacker node).

## Phase 1: Realistic Traffic / Log Mode

**Setup:**
- Run `Mostlylucid.BotDetection.Demo` on `http://localhost:5080`
- Policy: `fastpath` (Heuristic, no LLM, no blocking)
- API key: `SB-K6-FULL-DETECTION` (logonly - all traffic passes through, everything recorded)

**k6 script:** `scripts/k6/k6-local-logmode.js`  
Traffic mix (weighted): 40% Chrome human, 15% curl bot, 15% Python scraper, 10% Googlebot, 10% headless Chrome, 10% security scanner (Nikto/sqlmap UA).  
Paths hit: `/`, `/about`, `/docs`, `/api/health`, `/.env`, `/wp-login.php`, `/admin-secret`.  
Ramp: 1 VU -> 20 -> 50 -> 100 over 4 minutes, hold 2 minutes, ramp down.  
Think time: human profiles 0.5-2s, bot profiles 0-0.3s.  

**Metrics captured from `X-Bot-` headers:**
- `X-Bot-IsBot` (true/false)
- `X-Bot-Confidence` (0.0-1.0)
- `X-Bot-Processing-Ms` (detection latency)
- Response status (200/403/429)

**Success criteria:** p95 detection latency <10ms, p99 <50ms, zero 5xx errors.

## Phase 2: LlamaSharp LLM in the Path

**Setup:**
- Same Demo app, policy: `demo` (full sync pipeline: fastpath + Heuristic slow path + LLM wave)
- LlamaSharp Metal backend (M5 GPU) with Phi-3 mini 3.8B Q4_K_M GGUF
- `LLamaSharp.Backend.Cpu` -> `LLamaSharp.Backend.Metal` in csproj

**k6 script:** `scripts/k6/k6-local-llm.js`  
Same traffic mix as Phase 1. Ramp: 1 VU -> 10 -> 20, hold 2 minutes.  
Lower VU ceiling because LLM is synchronous and ~200-500ms per call.  
Adds custom metric `llm_escalation_rate` (requests that hit the AI path).

**Success criteria:** Identify per-request LLM overhead. Confirm Heuristic fast path correctly deflects obvious bots before LLM.

**Model path config:** `appsettings.Development.json` `LlamaSharp.ModelPath` pointing to a local GGUF.

## Phase 3: DDoS / Edge Flood

**Setup:**
- Switch to `Stylobot.Gateway` (YARP) on `http://localhost:8090`
- Policy: `realfast` (fast-path only, no LLM, block at 0.70)
- No API key bypass - bots get blocked/holodeck

**k6 script:** `scripts/k6/k6-local-ddos.js`  
Pure flood: no think time (`sleep(0)`), 100% bot traffic (curl UA, sqlmap, headless Chrome, random path probes).  
Ramp: 50 VU -> 200 -> 500 -> 1000, aggressive 30s steps.  
Thresholds: `http_req_failed < 0.01` (detection must hold), `http_req_duration p(95) < 100ms`.

**Goal:** Find where detection saturates vs where the ASP.NET pipeline saturates. Given detector benchmarks (<500ns each), detection itself should NOT be the bottleneck - the limit should be I/O or SQLite writes.

## Phase 4: Multi-Machine (Future)

**Topology:**
- k6 coordinator on Mac (orchestrates distributed test via `k6-operator` or manual `--out` aggregation)
- Attacker node 1: Mac (high VU, broadband)
- Attacker node 2: Raspberry Pi (low VU, ARM, slower clients - simulates underpowered edge nodes)
- Target: Demo app or Gateway on Mac

**Script:** `scripts/k6/k6-distributed.js` - same DDoS profile, split VU budget across nodes.  
Pi-specific: uses `--vus 10` and slower ramp (tests detection correctness at low concurrency from a geographically separate source address).

**Interesting signals:** Pi's ARM network stack will produce different TLS/TCP fingerprints than Mac. The TLS/TCP/H2 fingerprint detectors will see this as a distinct device class. Good stress test for false positive rate.

## File Manifest

| File | Purpose |
|------|---------|
| `scripts/k6/k6-local-logmode.js` | Phase 1: realistic mixed traffic, log only |
| `scripts/k6/k6-local-llm.js` | Phase 2: LLM in path, latency profiling |
| `scripts/k6/k6-local-ddos.js` | Phase 3: flood mode, no think time |
| `scripts/k6/k6-distributed.js` | Phase 4: multi-machine harness |
| `Mostlylucid.BotDetection.Llm.LlamaSharp/Mostlylucid.BotDetection.Llm.LlamaSharp.csproj` | Swap Cpu -> Metal backend |
| `Mostlylucid.BotDetection.Demo/appsettings.Development.json` | LlamaSharp model path config |

## Run Commands

```bash
# Phase 1
k6 run scripts/k6/k6-local-logmode.js

# Phase 2 (needs LlamaSharp configured + model downloaded)
k6 run scripts/k6/k6-local-llm.js

# Phase 3 (Gateway must be running on :8090)
k6 run scripts/k6/k6-local-ddos.js
```
