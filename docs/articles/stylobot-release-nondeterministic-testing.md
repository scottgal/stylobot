# StyloBot Release Series: Testing the Non-Deterministic

*Why a probabilistic, behaviour-based system resists `Assert.Equal`, what the BDF (Behavioural Definition Format) scenario file lets you assert instead, and the unusual side benefit of having one file that drives both regression and load.*

---

## The shape of the problem

Most testing frameworks assume the system under test is a function: input X returns output Y, and tests pin the function in place. Change the behaviour, a red bar appears, somebody investigates.

StyloBot is not a function. It is a probabilistic system that produces a posterior over behavioural evidence. The verdict for a single request depends on what other requests this fingerprint has made in the last few minutes. EWMA reputation drifts continuously. The metastable fingerprint matcher resolves a noisy vector to a stable identity by a two-pass match whose Pass 2 can flip Pass 1's allocation. A single observation moves the running verdict 15 percent toward the new signal; the previous 85 percent is whatever the fingerprint did before.

The right question for systems like this is not "does it return Y for X?" but "does it converge to the right neighbourhood, for the right reason, in a bounded number of steps?" That does not fit naturally into an `Assert.Equal` call.

## Why unit tests miss the failure class

The first version of this work had hundreds of per-detector unit tests with mock contexts and canned headers. They were fast, deterministic, and blind to the failure class I cared about.

The orchestrator merges contributions into a single `ev.Signals` dictionary that downstream consumers (dashboard, persistence, narrative builder, threat report) read from. A refactor that drops `primary_signature` from the merged surface fails no per-detector unit test: the detector still ran, the contribution still carried the signal, it just stopped reaching anyone who needed it. The dashboard's fingerprint table goes blank. Persistence skips the row. The unit suite stays green because none of it goes near the merge.

The integration test at `src/Mostlylucid.BotDetection.Orchestration.Tests/Integration/BdfReplayTests.Integration.cs` is direct about why it exists:

> This rig exists because the failure class it catches (downstream consumers of `ev.Signals` degrading silently when the orchestrator stops merging signals) does not fail any unit test.

The orchestrator is not a function; it is a pipeline whose value is whatever comes out of the merge after every contributor has run. The only way to assert on that is to run a real request through a real orchestrator and probe the merged surface. Mocking the merge defeats the test.

## BDF: a behavioural definition, not a scripted test

A BDF (Behavioural Definition Format) file describes a *behavioural shape*, not a fixed playback. The interesting parts of the schema are statistical: a client profile that captures distributional identity, a timing profile that defines a burst-with-jitter sampling rule rather than fixed delays, an evidence array of weighted predicates over behavioural signals, and a confidence prior. Here is a real signature (`bot-signatures/python-requests-bdf.json`):

```json
{
  "scenarioName": "python-requests-bdf",
  "scenario": "A bot/scraper using python-requests/2.31.0 with specific behavior patterns.",
  "confidence": 0.85,

  "clientProfile": {
    "userAgent": "python-requests/2.31.0",
    "cookieMode": "none",
    "headerCompleteness": "minimal",
    "clientHintsPresent": false,
    "robotsConsulted": false
  },

  "timingProfile": {
    "burstRequests": 10,
    "delayAfterMs":      { "min":   20, "max":   150 },
    "pauseAfterBurstMs": { "min":  500, "max":  2000 }
  },

  "requests": [
    { "method": "GET",  "path": "/",              "expectedStatusAny": [200,301,302], "expectedOutcome": "indexing" },
    { "method": "HEAD", "path": "/admin",         "expectedStatusAny": [200,403],     "expectedOutcome": "indexing" },
    { "method": "GET",  "path": "/api/data?page=1", "expectedStatusAny": [200,403],   "expectedOutcome": "indexing" },
    { "method": "GET",  "path": "/api/data?page=2", "expectedStatusAny": [200,403],   "expectedOutcome": "indexing" },
    { "method": "GET",  "path": "/api/data?page=3", "expectedStatusAny": [403,404],   "expectedOutcome": "indexing", "successCondition": "any 4xx" }
  ],

  "labels": ["Scraper", "RobotsIgnore"],

  "evidence": [
    { "signal": "interval_ms_p95", "op": "<", "value": 200,                "weight": 0.35 },
    { "signal": "requestInterval", "op": "<", "value": "burst <150ms",     "weight": 0.70 }
  ],

  "patterns":  { "requestInterval": "burst <150ms" },
  "reasoning": "The scraper accesses various endpoints and enumerates API paths while testing different HTTP methods."
}
```

Most of the surface is statistical, and most of it is what makes BDF a *definition* rather than a test script.

**`confidence` is a prior, not an assertion.** 0.85 says "this should land high-confidence bot when the system is healthy". The rig does not check the matured score equals 0.85; it checks the verdict lands on the bot side of the boundary. The prior is the band the signature's author (LLM or human) thinks the system should reach. Drift here is a calibration story, not a unit-test failure.

**`clientProfile` is a behavioural shape.** `cookieMode: none` is a *category* of behaviour (no cookie jar, every request starts fresh), not a specific header. `headerCompleteness: minimal` says "a request from this client carries only what curl-class libraries set", a fact about the population of requests this client emits, not a fixed header list. The k6 converter materialises these into header bundles at run time; the BDF stores the *kind* of client.

**`timingProfile` is a sampling rule.** `burstRequests: 10` plus `delayAfterMs: {min: 20, max: 150}` plus `pauseAfterBurstMs: {min: 500, max: 2000}` defines a generator: ten requests with uniform-random gaps in 20 to 150ms, then a uniform-random pause of 500 to 2000ms, repeat. The same BDF replayed twice produces two different request streams with the same statistical shape. That is the actual behaviour StyloBot's periodicity detector and session-vector compactor are trying to recognise; a fixed delay vector would test a different distribution entirely.

**`evidence` is a weighted predicate over signals.** Each entry is a claim of the form `signal OP value, weight w`. `{signal: "interval_ms_p95", op: "<", value: 200, weight: 0.35}` says "the p95 inter-request interval for this scenario should be under 200ms, and this is worth 0.35 of the verdict". The BDF asserts at the statistical level: not "request 7 returned bot=true", but "the population this client generates should produce an interval distribution whose p95 falls below 200ms". A scenario whose evidence claims diverge from what the running system measures is a signature that has drifted out of calibration.

**`labels` are taxonomy.** `[Scraper, RobotsIgnore]` is the class the scenario was generated for. Labels drive scenario selection in the load harness (run only `Scraper` scenarios, exclude `RobotsIgnore`) without anyone writing a regex over scenario names.

**`requests` describe what the client does, not what should happen next.** `expectedStatusAny: [200, 403]` tolerates either a successful fetch or an outright block, because both are valid productions of a hostile path probe. `expectedOutcome: indexing` is the *client's intent* (enumerate API pages), not the server's response. `successCondition: "any 2xx"` is the client's heuristic for "did this work": a scraper that gets 4xx on `?page=3` is succeeding at its enumeration job (it has discovered the cliff). The BDF captures the asymmetry between what the client is trying to do and what the system is supposed to do about it.

The "definition" part of the acronym is load-bearing. The signatures under `bot-signatures/` were generated by a model (Ministral 3B, per the directory's README) given prompts like "describe the behaviour of an X scraper hitting these endpoints". The model produces a structured behavioural definition; the converter materialises it into traffic. The same surface is open to a human author: write a BDF for a new scraper family, get both a regression scenario and a load-test entry without writing two pieces of code.

A slimmed-down replay form lives under `test-suites/{bots,humans,adversarial}/*.bdf.json` keeping only `requests[].method/path/headers/delayAfter` plus a soft `expectedDetection`. That subset is what the integration rig posts to the replay endpoint, which has no way to synthesise distributional behaviour from a single replay (no concurrent VUs, loopback only, so TLS/TCP fingerprint dimensions degrade by construction). The richer form drives the load harness, where concurrent VUs can actually realise the timing distribution.

## The replay endpoint runs through the real orchestrator

The integration rig posts each scenario to `POST /bot-detection/bdf-replay/replay`, an endpoint that lives in the product (`Mostlylucid.BotDetection/Endpoints/BdfReplayEndpoints.cs`), not in the test project. That placement is load-bearing: the endpoint resolves `IDetectionOrchestrator` from DI and runs through whichever orchestrator is currently registered, under `DetectionPolicy.Default`. The previous version hardcoded a specific orchestrator and masked regressions in the alternative path; rewiring to honour DI fixed that.

One deliberate policy override: the per-signature verdict cache is disabled for replay.

```csharp
var replayPolicy = Policies.DetectionPolicy.Default with
{
    SignatureCache = Policies.DetectionPolicy.Default.SignatureCache with { Enabled = false }
};
```

The cache's Skip path bypasses the full pipeline once a signature has a confident cached verdict. In production that is exactly what you want; for a rig measuring detection accuracy and signal flow it hides the per-request behaviour the rig is trying to assert on. Replay turns it off and every request runs the full waveform.

Scenarios are also isolated from each other. Every scenario gets a unique synthetic IP derived from a deterministic xxHash of its name (`192.0.<hash>.<hash>` from TEST-NET), so subnet-level reputation never bleeds between scenarios. And the rig calls `POST /bot-detection/bdf-replay/reset-identity` before each scenario to truncate the fingerprint store; without that, scenario N inherits the fingerprints scenarios 1..N-1 created and the per-scenario stability assertions become ordering-dependent.

## Asserting on a non-deterministic surface

The rig makes three assertions on each scenario. Each one is a template for testing systems like this.

**Matured verdict, not per-request verdict.** Bot scenarios assert `last.Actual.IsBot` is true; human scenarios assert the *majority* of requests classified as human. Asserting on every individual request would couple the test to the EWMA trajectory; relaxing to "settled at the end" tests the actual contract.

```csharp
var humanCount = response.Results.Count(r => r.Actual is { IsBot: false });
var botCount   = response.Results.Count - humanCount;
Assert.True(humanCount >= botCount,
    $"{response.ScenarioName}: {botCount}/{response.Results.Count} requests classified as bot, " +
    $"expected majority human. Last verdict: {last.Actual!.RiskBand} prob={last.Actual.BotProbability:F2}");
```

**Named signal probes, not signal counts.** Signal flow is probed per key, not by total. A count assertion is brittle: a new detector that emits a new signal masks the loss of a critical existing one (count stays the same; missing-key identity is invisible). A per-key probe names the consumer that breaks, in the failure message.

```csharp
Assert.True(probes.TryGetValue(SignalKeys.PrimarySignature, out var hasSig) && hasSig,
    $"{scenarioName}: {SignalKeys.PrimarySignature} missing from ev.Signals: " +
    "RequestPersistenceService skips persistence, dashboard fingerprint table goes blank");
```

The failure message is the test's spec. Three months later you do not have to remember why the signal mattered; the assertion tells you.

**Bounded convergence, not exact equality.** The metastable fingerprint matcher resolves a noisy vector to a stable identity. Vector composition includes session dimensions (path entropy, session age) that drift per request, so the two-pass match can occasionally fall outside its loose band and allocate. Asserting on a single fingerprint id across all requests would be wrong; asserting "no holes, and convergence to no more than `ceil(N/2)` distinct fingerprints" is the actual contract.

```csharp
var distinctFps = withFingerprints
    .Select(r => r.Actual!.IdentityFingerprintId!)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .Count();
var allowed = Math.Max(1, (int)Math.Ceiling(response.Results.Count / 2.0));
Assert.True(distinctFps <= allowed,
    $"{scenarioName}: {distinctFps} distinct fingerprints across {response.Results.Count} requests " +
    $"(allowed {allowed}). The matcher isn't converging: every request is allocating new, suggesting " +
    "vector composition is unstable or LooseThreshold is unreachable.");
```

`ceil(N/2)` is not magic. It encodes a policy: the first request always allocates; subsequent requests should mostly match via L1 confirm or Pass 2. Occasional allocation under high path variance is acceptable; allocation on *every* request is a regression. The bound is loose enough to absorb the noise the matcher is designed to absorb, tight enough to catch the failure mode where it stops converging at all.

All three patterns share a property: they assert on the contract the behaviour is supposed to satisfy, not on the specific numbers the current implementation happens to produce. When the implementation changes, the test still holds if the contract still holds. That is what makes a non-deterministic test stable.

## The side benefit: one file for accuracy *and* load

A BDF file is just JSON. The integration rig consumes the slim form. The load harness consumes the full statistical form: `scripts/convert-bdf-to-k6-v2.csx` reads a directory of signatures and emits a k6 script that *re-samples* each signature's distribution per VU per iteration.

Re-samples is doing real work in that sentence. The k6 script is not replaying a captured trace; it is realising the `clientProfile` and `timingProfile` as a live generator. Every VU iteration picks a signature, builds headers from its `headerCompleteness` and `clientHintsPresent` flags, attaches a cookie jar matching its `cookieMode`, fetches `robots.txt` if `robotsConsulted` is true, then draws fresh per-request delays from `delayAfterMs.min..max` and a fresh inter-burst pause from `pauseAfterBurstMs.min..max`. Two VUs running the same signature emit two different request streams with the same statistical shape, which is exactly what the detection pipeline is supposed to recognise as one *kind* of client.

```javascript
// Each VU picks a random signature and replays it with re-sampled burst/jitter.
// Concurrent VUs provide natural request interleaving.
export default function() {
    const sig = signatures[Math.floor(Math.random() * signatures.length)];
    // robots.txt, cookie jar, header bundle built from sig.clientProfile

    for (let i = 0; i < sig.requests.length; i++) {
        http.request(req.method, url, null, params);
        if (requestCount < sig.timingProfile.burstRequests) {
            sleep(randomBetween(delayMin, delayMax));
        } else {
            sleep(randomBetween(pauseMin, pauseMax));
            requestCount = 0;
            burstRate.add(1);
        }
    }
}
```

The headline property: **the corpus you test for correctness is the corpus you stress for performance**. There is no "integration tests pass but production traffic doesn't look like the integration tests". The scenario files *are* the traffic generator. When a customer reports a missed bot family, you add one BDF and it joins both the regression suite and the load test. No translation, no second source of truth, no drift.

The k6 metrics speak the same language as the BDF surface:

| k6 metric | What it measures |
|---|---|
| `bot_scenarios` / `human_scenarios` | Counters per scenario class |
| `detection_rate` | Fraction of bot-class scenarios flagged at the edge |
| `interval_ms` | Inter-request gap trend; checks the timing profile holds under load |
| `burst_detected` | Burst boundary hits derived from the timing profile |
| `http_req_duration` | Standard latency histogram for p95/p99 thresholds |

Thresholds on these become an executable spec for the load envelope:

```javascript
thresholds: {
    http_req_duration: ['p(95)<1000'],
    http_req_failed:   ['rate<0.1'],
    detection_rate:    ['rate>0.3'],
}
```

A refactor that regresses detection accuracy under load (verdict cache watchdog skipping requests it shouldn't) trips `detection_rate`. A refactor that introduces a slow path under contention trips `http_req_duration p95`. Same source data, two regressions caught.

## Calibration: the third use of the same file

The `evidence` array is itself testable, and this is the BDF use I find more interesting than either the rig or the load harness.

Each evidence entry is a claim of the form `signal OP value, weight w`. Once a signature has been replayed (under loopback or k6), the system has produced a measured distribution for the same signals. The `interval_ms_p95 < 200` claim is checkable against the measured p95. `cookies_count >= 2` is checkable against the cookie count the request actually carried. `clientHints_count >= 3` is checkable against the Sec-CH headers that landed.

When measured diverges from claimed, the signature has drifted out of calibration. Either it was overspecified for the system it was authored against, or the system has moved underneath it. Both are useful: the first says re-generate the signature from observation; the second says a refactor moved the statistical surface in a way no functional test would catch.

This turns the BDF from a regression artefact into a calibration artefact. The signatures under `bot-signatures/` were LLM-generated against an earlier version of the detector pipeline; their evidence claims encode what *that* version thought was the distinguishing statistical surface of each client family. Re-running calibration today tells you which claims still hold and which have aged out, the same way an EWMA decays a pattern that hasn't been seen. The corpus self-audits.

## Six rules for testing non-deterministic systems

Non-deterministic systems do not require non-deterministic tests; they require differently shaped ones. The shapes that work for StyloBot generalise:

1. **Define the input as a distribution, not a trace.** A `timingProfile` with min/max gaps is a generator; a captured request trace is one draw from it. Test against the trace and you test the draw, not the distribution. Same logic for the client profile (cookie mode and header completeness describe a population, not a fixed header list).
2. **Express the contract as weighted predicates.** The `evidence` array is the closest thing the system has to a unit-test assertion, and the predicates are over distributional signals (`interval_ms_p95 < 200`), not point values. Predicates with weights compose; equality assertions don't.
3. **Assert on the destination, not the path.** For systems whose state is an EWMA-smoothed posterior, per-step assertions couple to implementation; matured-state assertions couple to contract.
4. **Probe the merged surface, not the components.** The failure class mocks cannot catch is the one where components are individually correct but composition drops something. Run the full pipeline; probe per key; name the consumer in the failure.
5. **Bound the convergence, do not fix it.** A matcher that resolves noisy input to a stable identity will occasionally allocate. The assertion is "stays under the bound", chosen from the policy, not from the observed numbers.
6. **Share the input format across rig, load, and calibration.** When the scenario is a structured behavioural definition rather than a script, the same file drives a regression rig, a perf harness, and a calibration audit. The corpus does not split.

## Where this fits in the release series

The reliability article was about bounding memory so the system survives long-running deployments. The learning article was about making detection cheaper on repeat traffic without making it brittle. This one is about how the system gets verified at all.

All three answer the same question from different angles: *what does it take to ship a probabilistic, behaviour-based system that doesn't need a babysitter?* Bounded memory is necessary but not sufficient. A four-tier learning system is necessary but not sufficient. A test suite that catches the failure modes the system actually has (drift in a measured distribution, a dropped signal in the merge, a matcher that has stopped converging) rather than the ones a stateless function would have, is the third leg.

The BDF format earned its name by being a *definition* of behaviour, not a recording of one. Client profile, timing profile, weighted evidence, a confidence prior, taxonomy labels: each field captures a behavioural fact about a class of client at the level the detection pipeline is trying to recognise. The integration rig consumes the slim form, the load harness re-samples the full form, the evidence array opens a calibration loop the system can audit itself against. The maintenance cost of the test suite collapses into the maintenance cost of the signature corpus, and the signature corpus is something an LLM can co-author from a description of an attacker.

---

*The BDF replay rig lives at `src/Mostlylucid.BotDetection.Orchestration.Tests/Integration/BdfReplayTests.Integration.cs`. Slim replay scenarios are under `test-suites/{bots,humans,adversarial}/*.bdf.json`; the full statistical signatures (with `clientProfile`, `timingProfile`, `evidence`) are under `bot-signatures/*.json`. The k6 converter is `scripts/convert-bdf-to-k6-v2.csx`. The replay endpoint that both rigs use is `src/Mostlylucid.BotDetection/Endpoints/BdfReplayEndpoints.cs`. The signal contract these tests defend is documented in `docs/architecture/signal-contracts.md`.*
