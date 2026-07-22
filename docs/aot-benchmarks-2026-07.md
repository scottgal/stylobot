# AOT Performance Benchmarks — 2026-07

Owner: `bench-`. Refreshes the stale pre-atom-refactor per-detector table (the old harness no longer
exists). Two halves: **A** local NativeAOT micro-benchmarks (BenchmarkDotNet), **B** hardware
load/plateau/stress macro set on the shipped linux-arm64 AOT gateway.

> **verify-before-checkin:** every number below is pasted from an actual BenchmarkDotNet run. Each
> row is labelled by the runtime that produced it — `.NET 10 JIT` (RyuJIT), `NativeAOT 10.0`
> (osx-arm64, M5), or `NativeAOT 10.0 (Pi, linux-arm64)` where noted. No number is estimated.

## TL;DR

- The BenchmarkDotNet **NativeAOT toolchain works** for this project (`--runtimes nativeaot10.0`
  produces a genuine native binary and runs each `[SimpleJob]` benchmark under **both** the JIT
  baseline and NativeAOT — a free JIT-vs-AOT comparison).
- **NativeAOT is not uniformly slower.** It's workload-dependent:
  - SIMD / vectorized / crypto paths are AOT-**neutral or faster** (WeightedCosine L2 walk is *faster*
    under AOT; Ed25519 verify is within noise).
  - Allocation-heavy dictionary-building paths **regress** under AOT (AdaptiveWeighter.ComputeWeights
    2.7x slower + 53% more allocation).
  - `Expression.Compile` compiled-delegate paths are **catastrophically pessimized** (see finding
    below) because NativeAOT has no dynamic codegen and falls back to the expression interpreter.
- The AOT win is **startup + no JIT warm-up + memory footprint + single-file deployability**, not
  peak steady-state micro-throughput. Absolute latencies below are all sub-microsecond to low-µs;
  none is a gateway concern.

## Machine & toolchain (Half A, local)

| | |
|---|---|
| Host | Apple **M5**, 10 physical / 10 logical cores, macOS Tahoe 26.5 |
| RID | **osx-arm64** (see caveat) |
| SDK / runtime | .NET SDK 10.0.201, runtime 10.0.5 (10.0.526.15411), Arm64 |
| BenchmarkDotNet | 0.15.8 |
| AOT toolchain | NativeAOT 10.0, Latest ILCompiler, `microsoft.netcore.app.runtime.nativeaot.osx-arm64` |
| Source commit | FOSS `a7892ea3` (branch `agent/bench` off origin/main) |
| Run command | `dotnet run -c Release -- --filter '<suite>' --runtimes nativeaot10.0` |

> **Platform caveat (do not read local ns as Pi latency).** These local micros are **osx-arm64 AOT on
> an M5**. The gateway **ships linux-arm64 AOT** on a Raspberry Pi 5 (Cortex-A76), a different
> microarchitecture that is far slower per core than the M5. The value of the local set is the
> **JIT→AOT codegen/allocation delta**, not the absolute nanoseconds. The authoritative arm64 latency
> is the on-Pi run (Half B, pending). BenchmarkDotNet can only AOT-compile for the *host* platform, so
> linux-arm64 micros cannot be produced on this Mac.

## Half A — local micro-benchmarks (JIT vs osx-arm64 AOT)

Ratio column = AOT mean ÷ JIT mean (>1 = AOT slower). Allocations are managed-only per op.

### Identity hot path (per-request metastable-fingerprint layer)

| Method | JIT ns | AOT ns | AOT/JIT | JIT alloc | AOT alloc |
|---|---:|---:|---:|---:|---:|
| WeightedCosine L1 confirm (layout-dim) | 25.7 | 22.9 | **0.89** | 0 | 0 |
| WeightedCosine L2 walk (TopK=5) | 193.4 | 122.9 | **0.64** | 0 | 0 |
| Encode Chrome navigation (full layout) | 1708.8 | 1404.7 | **0.82** | 544 B | 544 B |
| Encode Chrome XHR (full layout) | 1622.3 | 1317.8 | **0.81** | 544 B | 544 B |
| EncoderResultCache hit | 16.8 | 18.7 | 1.11 | 0 | 0 |
| EncoderResultCache miss + populate | 59.3 | 111.4 | 1.88 | 56 B | 56 B |
| BrowserModePredicate.Matches navigation (hit) | 22.2 | 21.2 | 0.96 | 0 | 0 |
| BrowserModePredicate.Matches xhr (hit) | 5.0 | 8.9 | 1.80 | 0 | 0 |
| BrowserModeRegistry.Classify navigation (full walk) | 74.0 | 139.7 | 1.89 | 0 | 40 B |
| BrowserModeRegistry.Classify xhr (full walk) | 71.8 | 137.6 | 1.92 | 0 | 40 B |

The core vector match (WeightedCosine, IdentityVectorEncoder) — the actual per-visitor hot path — is
**faster under AOT**. The registry-walk classifier regresses ~1.9x and picks up small allocation.

### Markov / adaptive-weighting / geo-similarity hot paths

| Method | JIT ns | AOT ns | AOT/JIT | JIT alloc | AOT alloc |
|---|---:|---:|---:|---:|---:|
| RecordTransition (per-request) | 3250.3 | 4830.3 | 1.49 | 9976 B | 9800 B |
| PathNormalizer.Normalize (per-request) | 130.7 | 197.2 | 1.51 | 104 B | 104 B |
| PathNormalizer.Normalize (8 diverse) | 1046.5 | 1652.0 | 1.58 | 648 B | 648 B |
| PathNormalizer.Classify | 23.6 | 20.1 | 0.85 | 0 | 0 |
| ComputeSimilarity (default weights) | 243.5 | 335.0 | 1.38 | 776 B | 776 B |
| ComputeSimilarity (adaptive weights) | 82.5 | 123.8 | 1.50 | 0 | 0 |
| ComputeGeoSimilarity (Haversine) | 13.4 | 15.9 | 1.19 | 0 | 0 |
| ComputeGeoSimilarity (categorical) | 1.9 | 2.8 | 1.52 | 0 | 0 |
| **AdaptiveWeighter.ComputeWeights (50 features)** | 7615.7 | 20398.2 | **2.68** | 24104 B | **36888 B** |
| ComputeSimilarity 50x50 matrix | 106163.7 | 152135.7 | 1.43 | 0 | 0 |
| JensenShannonDivergence (5-key) | 261.1 | 423.2 | 1.62 | 800 B | 800 B |
| TransitionMatrix.RecordTransition | 56.9 | 45.8 | 0.81 | 0 | 0 |
| TransitionMatrix.GetTransitionProbability | 21.9 | 28.6 | 1.31 | 0 | 0 |
| TransitionMatrix.GetDistribution | 53.0 | 74.2 | 1.40 | 432 B | 432 B |
| TransitionMatrix.GetPathEntropy | 175.9 | 225.2 | 1.28 | 1016 B | 1016 B |
| DecayingCounter.Decayed | 46.8 | 46.7 | 1.00 | 0 | 0 |

`AdaptiveWeighter.ComputeWeights` is the worst allocation-and-time regression (dictionary-building,
LINQ) — 2.68x slower and 53% more allocation under AOT. Candidate for AOT-aware tuning (foss-).

### Pattern normalization (Wave 0 per-request) + reputation

| Method | JIT ns | AOT ns | AOT/JIT | alloc (both) |
|---|---:|---:|---:|---:|
| NormalizeUserAgent (Chrome/Win) | 111.4 | 137.8 | 1.24 | 240 B |
| NormalizeUserAgent (Chrome/Mac) | 120.7 | 148.6 | 1.23 | 240 B |
| NormalizeUserAgent (Googlebot) | 132.0 | 184.1 | 1.39 | 224 B |
| NormalizeUserAgent (curl) | 76.0 | 139.5 | 1.83 | 216 B |
| NormalizeUserAgent (Python scraper) | 115.7 | 173.0 | 1.49 | 264 B |
| CreateUaPatternId (Chrome/Win) | 140.3 | 179.8 | 1.28 | 360 B |
| CreateIpPatternId (IPv4) | 26.6 | 26.7 | 1.01 | 112 B |
| CreateIpPatternId (IPv6) | 84.7 | 85.8 | 1.01 | 480 B |
| ApplyEvidence (existing pattern) | 43.5 | 48.3 | 1.11 | 144 B |
| ApplyEvidence (new pattern) | 62.3 | 70.1 | 1.13 | 144 B |
| ApplyTimeDecay (30min stale) | 20.7 | 20.3 | 0.98 | 0 |

Allocations identical JIT↔AOT throughout; string-normalization regresses modestly under AOT.

### Response broadcast / dashboard aggregation

| Method | JIT ns | AOT ns | AOT/JIT | JIT alloc | AOT alloc |
|---|---:|---:|---:|---:|---:|
| Aggregate (5 contributions) | 233.9 | 310.0 | 1.33 | 2024 B | 2024 B |
| Aggregate (15 — typical request) | 598.6 | 853.9 | 1.43 | 4680 B | 4680 B |
| Aggregate (40 — heavy request) | 1322.9 | 1732.6 | 1.31 | 8504 B | 8504 B |
| Compiled-delegate CountryCode accessor (old, unguarded) | 2.7 | **55.3** | 20.4 | 0 | 152 B |
| Reflection PropertyInfo CountryCode (baseline) | 5.8 | 9.7 | 1.67 | 0 | 0 |
| **Guarded CountryCode accessor (shipped fix)** | **2.9** | **9.8** | **3.35** | **0** | **0** |
| LINQ baseline aggregate (15) | 1381.6 | 2563.9 | 1.86 | 6448 B | 7992 B |

> **✅ AOT finding — FOUND, FIXED, VERIFIED (issue `nativeaot-pessimizes-the-compiled-delegate-count`).**
> The unguarded compiled-delegate accessor was 2.7 ns / 0 B under JIT but **55.3 ns / 152 B under AOT —
> ~20x slower, and 5.6x slower than plain reflection (9.7 ns).** Cause: `Expression.Compile` needs
> dynamic IL codegen; NativeAOT has none, so it silently falls back to the expression *interpreter*.
> foss- fixed it (main `af0a12a0`): `DetectionBroadcastMiddleware.GetCountryCodeAccessor` now guards the
> `Expression.Compile` path behind `RuntimeFeature.IsDynamicCodeSupported` — a compile-time constant the
> ILC constant-folds, so the compiled branch is dead-code-eliminated under AOT (no interpreter fallback,
> no IL3050) and a cached `PropertyInfo` read is used instead. **Measured before/after under AOT: 55.3 ns
> / 152 B → 9.8 ns / 0 B** (the `Guarded` row above); JIT keeps the 2.9 ns compiled delegate. The
> `Guarded` benchmark is a regression guard — it tracks the shipped selection and would resurface the
> 55 ns interpreter cost if the guard is ever removed. Durable lesson: **avoid `Expression.Compile` on
> AOT hot paths; gate it behind `RuntimeFeature.IsDynamicCodeSupported` with a reflection/source-gen
> fallback.**

### Session-mode resolver (SessionModeResolverAtom, priority 15)

| Method | JIT ns | AOT ns | AOT/JIT | alloc (both) |
|---|---:|---:|---:|---:|
| Established-streaming hit (SignalR first) | 37.4 | 43.3 | 1.16 | 168 B |
| Non-streaming full walk (50-request session) | 234.9 | 341.7 | 1.45 | 248 B |
| No session (unknown signature) | 48.7 | 48.0 | 0.99 | 160 B |

### Session vector pipeline

| Method | JIT ns | AOT ns | AOT/JIT | JIT alloc | AOT alloc |
|---|---:|---:|---:|---:|---:|
| Encode 10 requests (small) | 958.8 | 1249.2 | 1.30 | 5208 B | 5384 B |
| Encode 50 requests (medium) | 3556.1 | 5072.6 | 1.43 | 17520 B | 17728 B |
| Encode 200 requests (large) | 11863.6 | 17890.8 | 1.51 | 35320 B | 35744 B |
| Encode 50 + fingerprint | 3970.0 | 5012.6 | 1.26 | 17520 B | 17728 B |
| Cosine similarity (118-dim) | 61.2 | 60.9 | 0.99 | 0 | 0 |
| Velocity computation (118-dim) | 52.9 | 50.0 | 0.94 | 544 B | 544 B |
| Velocity magnitude (118-dim) | 118.2 | 117.4 | 0.99 | 544 B | 544 B |
| Maturity computation (50 requests) | 327.6 | 700.5 | 2.14 | 848 B | 872 B |
| Full pipeline: encode+similarity+velocity | 4700.7 | 6841.0 | 1.46 | 23272 B | 23656 B |

Vectorized ops (cosine, velocity) are AOT-neutral; the encode/maturity dictionary work regresses.

### Web Bot Auth (RFC 9421 verify pipeline)

Higher warm-up variance (crypto), iterationCount=5.

| Method | JIT ns | AOT ns | AOT/JIT | alloc (both) |
|---|---:|---:|---:|---:|
| Rfc9421_Valid_Ed25519 | 21953.5 | 20638.6 | 0.94 | 4816 B |
| Rfc9421_Valid_EcdsaP256 | 80412.8 | 79401.9 | 0.99 | 6744 B |
| Rfc9421_Invalid_Sig | 20400.0 | 20943.8 | 1.03 | 4600 B |
| Rfc9421_Expired | 20291.6 | 20989.2 | 1.03 | ~5520 B |
| Rfc9421_Unknown_Key | 376.8 | 477.2 | 1.27 | 2784 B |
| Rfc9421_Malformed | 35.0 | 35.3 | 1.01 | 128 B |
| SignedToken_Valid | 21535.9 | 21613.2 | 1.00 | ~3296 B |
| SignedToken_Tampered | 21290.4 | 21793.2 | 1.02 | ~3232 B |
| Crypto_Baseline_Ed25519 | 20748.7 | 20165.4 | 0.97 | 56 B |
| | | | | |

Crypto-dominated → **AOT-neutral** (verification cost is the elliptic-curve math, not managed
codegen). This is the most AOT-friendly suite.

### WellKnownBotIndex (three-tier bot-UA matcher) — converted InProcess→external

Was pinned to `InProcessEmitToolchain` (in-host JIT, cannot AOT). Converted to `[SimpleJob]` external
so `--runtimes nativeaot10.0` yields an AOT column. **Runs cleanly under AOT** (verified in isolation;
in a *combined* run it must not share the generated AOT exe with the pipeline runner — see note below).

| Method | JIT ns | AOT ns | AOT/JIT | alloc (both) |
|---|---:|---:|---:|---:|
| ColdMiss (unique human UA, L1 exit) | 7021.3 | 12089.9 | 1.72 | 496 B |
| ColdHit (unique bot UA, L2 literal) | 6758.5 | 12711.8 | 1.88 | 416 B |
| RegexScan (L3 regex tier) | 17174.5 | 23843.5 | 1.39 | ~392 B |
| WarmCache (BoundedCache hit) | 26.3 | 32.8 | 1.25 | 0 |

Regex/literal-scan tiers regress ~1.4–1.9x under AOT (regex + span scanning is codegen-sensitive); the
cache-hit fast path stays flat. Allocations identical JIT↔AOT.

### SlimSimilarity (BoundedVectorCache + VectorMath SIMD + Slim* search) — converted, PARTIAL AOT

Also converted InProcess→external. JIT runs fully; **NativeAOT is partial** — the sync SIMD/cache-get
paths run, but `BoundedVectorCache.Touch`, `GetAll`, and the three `FindSimilarAsync` (async) paths
returned **NA under NativeAOT** (the AOT benchmark process aborted on `Touch` and BDN cascaded the
remainder of that AOT exe to NA; JIT values are intact). Reported honestly as partial rather than
dropped or faked. (CacheSize=100 shown; JIT scan cost scales with cache size — full 4-param table in
`BenchmarkDotNet.Artifacts/`.)

| Method (CacheSize=100) | JIT ns | AOT ns | AOT/JIT | JIT alloc |
|---|---:|---:|---:|---:|
| VectorMath.CosineSimilarity SIMD (129-dim) | 24.8 | 25.9 | 1.04 | 0 |
| VectorMath.IsValidVector (129-dim) | 49.3 | 49.5 | 1.00 | 0 |
| BoundedVectorCache.TryGet (hit) | 3.3 | 6.6 | 2.01 | 0 |
| BoundedVectorCache.TryGet (miss) | 2.0 | 33.4 | **16.7** | 0 |
| BoundedVectorCache.Touch | 17.8 | **NA** | — | 40 B |
| BoundedVectorCache.GetAll full scan | 1127.7 | **NA** | — | 120 B |
| SlimSignature.FindSimilarAsync (top5) | 3689.7 | **NA** | — | 224 B |
| SlimSession.FindSimilarAsync (top10) | 3309.3 | **NA** | — | 224 B |
| SlimSession.FindSimilarMahalanobisAsync (top10) | 4771.3 | **NA** | — | 5024 B |

The **SIMD vector math is AOT-neutral** (CosineSimilarity/IsValidVector within noise) — the core
similarity primitive costs the same under AOT. The dictionary-miss path regresses sharply (16.7x, a
few ns → 33 ns). The async-scan NAs are a follow-up item (BDN NativeAOT + these async/`Touch` paths);
not a product regression — the JIT path is unaffected and the gateway's actual similarity primitive
(CosineSimilarity) is AOT-clean.

### Could-not-AOT: `PipelineBenchmarkRunner` — confirmed, kept on JIT

The YAML-driven end-to-end runner (`Scenarios/*.benchmark.yaml` → reflection-based YamlDotNet
deserializer → full `AddBotDetection()` DI graph) is AOT-hostile. **Precise failure (measured):** the
NativeAOT binary *compiles and launches*, but at runtime **every scenario fails to deserialize** —
`Failed to load …/Scenarios/<name>.benchmark.yaml: Exception during deserialization` for all ~35
scenarios. YamlDotNet's reflection deserializer cannot construct `BenchmarkScenario` (object-valued
signal dict) under NativeAOT — the trimmer strips the reflection metadata it needs. With zero
scenarios loaded, the `[ParamsSource]` is empty and the pipeline benchmark cannot execute under AOT.
**Kept on the JIT job and documented rather than faked**, per methodology. (Operational note: run this
suite in a *separate* BDN invocation from the AOT-safe suites — sharing the generated NativeAOT exe
lets its empty/failing scenario set disrupt sibling suites in the same run, e.g. WellKnownBotIndex.)

> If a future AOT-clean end-to-end micro is wanted, the fix is to replace the reflection YamlDotNet
> loader with a VYaml/source-gen or hand-written scenario model (AOT-safe) — a `foss-` harness change,
> out of scope for this refresh.

## Half B — hardware macro set (load / plateau / stress) — PENDING

Executed by `deploy-` (sole hardware operator); bench- owns methodology + analysis. **Not yet run** —
gated on operator granting deploy- host-mutation permission on the Pi.

**Topology:** SUT = Pi `192.168.0.39` (Pi 5, linux-arm64, 4-core Cortex-A76) running the shipped AOT
gateway; k6 driver = Maxo `192.168.0.15` (k6 v1.3.0).

**SUT binary provenance (operator-mandated released artifact):**

| | |
|---|---|
| Release | stylobot `allbot-v8.1.7` (published 2026-07-17) |
| Commit | `bf15f84e18fb9a1eae8410a0d04ae10c6992d643` |
| Artifact sha256 | `865a4a307c55310e58749404cdc1e123c8d215c2b9b5542d921d216b3cfaa3df` |
| Binary | ELF aarch64, NativeAOT (verified `PublishAot=true`, `__managedcode` present, zero libcoreclr/hostfxr), stripped, 78,599,520 B, BuildID sha1 `2d3fa8cbc2f6c9c4f7c21f565890f3643e611de1` |

> **Micro↔macro version coherence.** Local/on-Pi micros run at `a7892ea3`; the macro SUT runs the
> released `v8.1.7` (`bf15f84e`). Perf-relevant delta across the 49 commits between = Analysis 0
> changes, Identity +13 lines (`SqliteFingerprintStore`, a store method — not a benchmarked hot
> path), Orchestration +21 lines (additive DI) = **+34 non-hot-path lines; all benchmarked hot paths
> are byte-identical.** The sets are coherent; the released artifact is arguably the better macro
> baseline (it is what ships).

**Detection policy for the runs:** keyed traffic uses the `soak-keys-bench.json` bench keys — all
`ActionPolicyName=logonly`, `DisableLearningWrites=true`, unlimited rate. So synthetic k6 traffic is
both **logonly** (no tarpit inflating latency — clean throughput) and **poison-guarded** (no centroid
training on the fresh corpus). Primary numbers use `X-SB-Api-Key=SB-BENCH-FULL` (all detectors); 7
per-detector ablation keys exist for an optional detector-family cost matrix.

**Three profiles** (right-sized for a 4-core Pi; the 300RPS/500VU default just queues it):

| Profile | Script | Shape | Measures |
|---|---|---|---|
| **load** | `scripts/k6/k6-pi-class.js` | constant-arrival 30 RPS 15m, then 50 RPS 15m | steady p50/p95/p99 + error rate at a sustainable rate |
| **plateau** | `scripts/soak/k6-plateau.js` | ramping-arrival 10→20→50→100→150 RPS, 90s holds (MAX_RPS=150; rerun 200 if no knee) | the sustainable ceiling / latency knee |
| **stress** | `scripts/k6/k6-break.js` | ramp 50→…→1000 then 50 cooldown | break/degradation point + recovery |

Output: k6 `--summary-export` JSON (the `soak-results/plateau-*.json` k6-summary shape) + end-of-run
Pi-side gateway RSS and SQLite `.db` sizes. `k6-break.js` was patched to send `X-SB-Api-Key` on every
request (poison-guard) before the stress run.

**Plus (stylobot- directive):** deploy- runs the **same BDN micro-suite on the Pi** for authoritative
linux-arm64 micros (`agent/bench` checked out on the Pi, the 9 AOT-safe suites, `--runtimes
nativeaot10.0`). That becomes the third micro column (JIT / osx-arm64 AOT delta-ref / linux-arm64 AOT
Pi authoritative). _Pending Pi availability._

## Proposed CLAUDE.md update (for stylobot- review — NOT applied here)

The current CLAUDE.md per-detector table is a pre-atom-refactor baseline from a deleted harness
(Intent/Heuristic/Behavioral/etc. detector rows that no longer map to the atom pipeline). Proposal:
replace it with a pointer to this doc plus a short "what AOT costs" summary, rather than a per-detector
ns table (the detectors are now atoms and the meaningful figures are the hot-path micros above + the
Half-B macro set). Exact diff to follow once the Pi macro/micro numbers land and the table is complete.
