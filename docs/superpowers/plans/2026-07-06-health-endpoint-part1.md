# Health-Endpoint Part 1 (source-aware health policy) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the gateway throttling its own health endpoints (Docker/LB/k8s probes → 429) by classifying real probes as `BotType.Internal`/"Health Probe" (shape+source), while keeping external health-endpoint enumeration in detection — without a path bypass.

**Architecture:** Fix the underlying reader bug (sink-only signals never reach `MergedSignals`, so six `is true` checks are dead), then add a health-endpoint recognizer, a shape-AND-source classifier that reuses the existing `BotType.Internal`→`logonly` lane, "Health Probe" naming, an external-recon signal that nudges `intent.threat_score`, and a `Source` matcher on `EndpointPolicyRule`.

**Tech Stack:** .NET 10, xUnit + Moq, atom orchestrator (`IDetectorAtom`), `SignalSink`, System.Text.Json source-gen.

**Spec:** `docs/superpowers/specs/2026-07-06-health-endpoint-design.md` (committed dcb7c9a7). Scope is **Part 1 only**; Parts 2/3 are tracked as tasks #12/#13.

## Global Constraints

- **Never skip detection.** Detection always runs; only classification+action change. No path bypass, no skip middleware.
- **No hardcoded lists in C#.** Health paths, probe UA families, expected-source CIDRs live in config/YAML (`BotDetection:HealthEndpoints`, `BotDetection:EndpointPolicies`), code is the dispatcher.
- **All settings configurable** (`feedback_all_settings_configurable`).
- **No em dashes** in code/comments/docs. **SQLite FOSS**, no new stores. **No `BackgroundService`** (use `ScheduleCoordinator` if a tick is ever needed — not in Part 1).
- **Read `IpIsLocal` (and the other five bool signals) from the `SignalSink` via `ReadBoolHint`**, never from `MergedSignals` (that is the bug being fixed).
- Verify locally before check-in; run the full suite + `verify-aot.sh` before declaring done.

---

### Task 1: Reader fix — thread the SignalSink into ToAggregatedEvidence

**Root cause (overview-confirmed):** `ledger.MergedSignals` is built only from `contribution.Signals`; `IpAtom` etc. raise via `sink.Raise` and never populate `contribution.Signals`, so `preSignals.TryGetValue(k,…)` fails on missing key and six `is true` checks are silently false in prod: `IpIsLocal` (`:154,:244,:490`), `UserAgentIsBot`/declaredBot (`:114`), `ReputationFastAbortActive` (`:835`), `SecurityToolDetected` (`:933`).

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/DetectionLedgerExtensions.cs` (signature `:32-39`; `preSignals` `:61-63`; the six checks; the helper methods taking `signals` for `:835/:933`; `CreateEarlyExitResult` `:357` + `earlySignals` `:391` for `:490`)
- Modify: `src/Mostlylucid.BotDetection/Orchestration/Atoms/BotDetectionOrchestrator.cs:174` (pass `_signalSink`)
- Test: `src/Mostlylucid.BotDetection.Test/Orchestration/DetectionLedgerReaderFixTests.cs` (new)

**Interfaces:**
- Consumes: `SignalSink.ReadBoolHint(string prefix, bool fallback=false)` (`Orchestration/Atoms/SignalHintExtensions.cs:23`).
- Produces: `ToAggregatedEvidence(this DetectionLedger, …, SignalSink? sink = null)` — a new trailing optional param; all six bool reads resolve from the sink when `premergedSignals` is not supplied.

- [ ] **Step 1: Write the failing test** — prove the production path (no `premergedSignals`) now honours a sink signal.

```csharp
// src/Mostlylucid.BotDetection.Test/Orchestration/DetectionLedgerReaderFixTests.cs
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

public sealed class DetectionLedgerReaderFixTests
{
    [Fact]
    public void LoopbackSinkSignal_PromotesToInternal_ViaProductionPath()
    {
        var sink = new SignalSink(maxCapacity: 128, maxAge: TimeSpan.FromMinutes(5));
        sink.Raise($"{SignalKeys.IpIsLocal}:true", "s");
        // A ledger that would otherwise classify the raw request as a bot (Tool-ish).
        var ledger = TestLedgerFactory.CurlLikeBot();   // helper: builds a ledger with a curl UA verdict

        // NOTE: no premergedSignals passed -> exercises the broken-in-prod fallback path.
        var evidence = ledger.ToAggregatedEvidence(options: new BotDetectionOptions(), sink: sink);

        Assert.Equal(BotType.Internal, evidence.PrimaryBotType);
    }
}
```
(If no `TestLedgerFactory` exists, build the ledger inline from a `DetectionLedger` with one contribution carrying a bot probability; the point is only that `IpIsLocal` from the sink flips `PrimaryBotType` to `Internal`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter FullyQualifiedName~DetectionLedgerReaderFixTests`
Expected: FAIL — `PrimaryBotType` is the raw bot type (e.g. `Tool`), not `Internal` (and the new `sink:` param doesn't compile yet).

- [ ] **Step 3: Add the `sink` param + read the six checks from it**

In `DetectionLedgerExtensions.cs`, add `SignalSink? sink = null` as the last param of `ToAggregatedEvidence` (`:39`) and `CreateEarlyExitResult` (`:357`). Replace each `preSignals.TryGetValue(k, out var v) && v is true` with a sink-first read, keeping the dict as the test-path fallback:

```csharp
// helper local (top of ToAggregatedEvidence)
bool ReadBool(string key) =>
    sink?.ReadBoolHint(key, fallback: false)
    ?? (preSignals.TryGetValue(key, out var v) && v is true);
```

Then: `:114` `var declaredBot = ReadBool(SignalKeys.UserAgentIsBot);`, `:154` `localIpForVerdict = ReadBool(SignalKeys.IpIsLocal);`, `:244` `isLocalIp = ReadBool(SignalKeys.IpIsLocal);`. For `:835/:933` (in `signals`-param helpers) and `:490` (`earlySignals` in `CreateEarlyExitResult`), thread `sink` into those methods and use the same `sink?.ReadBoolHint(key) ?? (signals.TryGetValue…)` shape. At `:174` in `BotDetectionOrchestrator`, pass `sink: _signalSink`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter FullyQualifiedName~DetectionLedgerReaderFixTests`
Expected: PASS.

- [ ] **Step 5: Full suite (guard the newly-live siblings)**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ src/Mostlylucid.BotDetection.Orchestration.Tests/`
Expected: PASS. If a previously-green test flips (a sibling check now firing — e.g. `DeclaredBot` verdict-honest override), inspect: is the new behaviour correct? Fix the test expectation if the behaviour is right; fix the read if not.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/DetectionLedgerExtensions.cs \
        src/Mostlylucid.BotDetection/Orchestration/Atoms/BotDetectionOrchestrator.cs \
        src/Mostlylucid.BotDetection.Test/Orchestration/DetectionLedgerReaderFixTests.cs
git commit -m "fix(orchestration): read sink bool signals in ToAggregatedEvidence (6 dead checks)"
```

- [ ] **Step 7: Ping overview** — drop `inbox/overview-follow-up-reader-fix-landed.md` with the SHA so they eyeball staging for the newly-live siblings (rollout note).

---

### Task 2: Health-endpoint recognizer + config catalog

**Files:**
- Create: `src/Mostlylucid.BotDetection/HealthEndpoints/HealthEndpointCatalog.cs` (+ `HealthEndpointOptions.cs`)
- Modify: `src/Mostlylucid.BotDetection/Models/DetectionContext.cs` (`SignalKeys.HealthEndpoint = "request.health_endpoint"`)
- Create: `src/Mostlylucid.BotDetection/Orchestration/Atoms/HealthEndpointAtom.cs` (Wave 0, raises `request.health_endpoint:true` on path match)
- Modify: `Extensions/ServiceCollectionExtensions.cs` (register atom + options) and the atom's `.detector.yaml` manifest (5-file checklist)
- Test: `src/Mostlylucid.BotDetection.Test/HealthEndpoints/HealthEndpointCatalogTests.cs`

**Interfaces:**
- Produces: `HealthEndpointCatalog.IsHealthPath(PathString path) : bool`; `SignalKeys.HealthEndpoint`; config `BotDetection:HealthEndpoints:Paths` (default `["/health","/healthz","/livez","/readyz","/ready","/live","/ping","/status","/alive","/admin/alive"]`).

- [ ] **Step 1: Failing test for the catalog**
```csharp
[Theory]
[InlineData("/health", true)]
[InlineData("/admin/alive", true)]
[InlineData("/api/products", false)]
public void IsHealthPath_MatchesDefaults(string path, bool expected)
    => Assert.Equal(expected, new HealthEndpointCatalog(HealthEndpointOptions.Default).IsHealthPath(path));
```
- [ ] **Step 2: Run — FAIL** (`HealthEndpointCatalog` undefined). `dotnet test … --filter HealthEndpointCatalogTests`
- [ ] **Step 3: Implement** `HealthEndpointOptions` (`List<string> Paths` with the default list) + `HealthEndpointCatalog` (case-insensitive exact/`StartsWith` set built once). Add `SignalKeys.HealthEndpoint`.
- [ ] **Step 4: Run — PASS.**
- [ ] **Step 5: Add `HealthEndpointAtom`** (Wave 0, `TriggerConditions` empty): reads `IHttpContextAccessor` path, if `catalog.IsHealthPath` raises `sink.Raise($"{SignalKeys.HealthEndpoint}:true", sessionId)`. Register in DI + add `healthendpoint.detector.yaml` manifest + narrative-builder entries (5-file checklist).
- [ ] **Step 6: Atom contract test** — add `HealthEndpointAtom` to the forward/reverse emit-contract coverage (Task #27 harness) so its emit stays declared.
- [ ] **Step 7: Commit** `feat(health): recognize health-endpoint paths (request.health_endpoint signal)`

---

### Task 3: Shape-AND-source classification -> Internal / allow

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/DetectionLedgerExtensions.cs` (`:246` promotion — extend so a health-endpoint + expected-source + probe-shape request classifies `Internal` even when the raw type is `Tool`)
- Create: `src/Mostlylucid.BotDetection/HealthEndpoints/ProbeShapeClassifier.cs` (positive probe-shape match: UA family list `kube-probe|Go-http-client|curl|wget|docker` from config + absence of browser `Sec-Fetch-*`/`Accept: text/html`)
- Test: `src/Mostlylucid.BotDetection.Test/HealthEndpoints/ProbeShapeClassifierTests.cs` + extend `DetectionLedgerReaderFixTests`

**Interfaces:**
- Consumes: `SignalKeys.HealthEndpoint`, `SignalKeys.IpIsLocal` (sink), `ProbeShapeClassifier.IsProbeShape(signals) : bool`.
- Produces: promotion to `BotType.Internal` when `health_endpoint && (IpIsLocal || trusted-proxy) && IsProbeShape`.

- [ ] **Step 1: Failing tests** — (a) loopback + `curl` UA + `/health` -> `Internal`; (b) **shape guard**: trusted IP + browser `Sec-Fetch-Mode: navigate` + `/health` -> NOT `Internal` (stays detected).
- [ ] **Step 2: Run — FAIL.**
- [ ] **Step 3: Implement** `ProbeShapeClassifier` (config `BotDetection:HealthEndpoints:ProbeUserAgents`) + extend the `:246` promotion: `var isHealthProbe = ReadBool(HealthEndpoint) && expectedSource && probeShape; var primaryBotType = (isLocalIp || isHealthProbe) ? BotType.Internal : …`. Expected-source = `ReadBool(IpIsLocal)` or `TransportHeaderTrust`-allowlisted peer.
- [ ] **Step 4: Run — PASS** (both, including the shape guard).
- [ ] **Step 5: Commit** `feat(health): shape+source classification of health probes as Internal`

---

### Task 4: External recon -> health.endpoint_recon + intent.threat_score nudge

**Files:**
- Modify: `HealthEndpointAtom.cs` (when `health_endpoint` && NOT expected-source-probe → raise `health.endpoint_recon:true`)
- Modify: the intent/threat-score contributor that reads honeypot-style signals (mirror `ProjectHoneypotAtom`/`EndpointHistoryAtom` — grep `intent.threat_score` / `IntentThreatScore` for the exact nudge site)
- Modify: `SignalKeys` (`HealthEndpointRecon = "health.endpoint_recon"`)
- Test: `src/Mostlylucid.BotDetection.Test/HealthEndpoints/HealthReconThreatScoreTests.cs`

**Interfaces:** Produces `SignalKeys.HealthEndpointRecon`; consumed by the intent threat-score aggregation as a small positive nudge (match `ProjectHoneypotAtom`'s magnitude).

- [ ] **Step 1: Failing test** — external `curl /health` co-occurring with another recon signal on the same source yields a higher `intent.threat_score` than either alone.
- [ ] **Step 2: Run — FAIL.**
- [ ] **Step 3: Implement** the recon raise (external branch) + wire `HealthEndpointRecon` into the threat-score nudge (same site/magnitude as `ProjectHoneypotAtom`).
- [ ] **Step 4: Run — PASS.**
- [ ] **Step 5: Commit** `feat(health): external health-endpoint recon nudges intent.threat_score`

---

### Task 5: `Source` matcher on EndpointPolicyRule + default health policy

**Files:**
- Modify: `src/Mostlylucid.BotDetection/EndpointPolicies/EndpointPolicyOptions.cs` (add `public string? Source { get; set; }` to `EndpointPolicyRule`, `internal|external|any`, default `any`)
- Modify: `src/Mostlylucid.BotDetection/EndpointPolicies/IEndpointPolicyResolver.cs` (`ConfigEndpointPolicyResolver.Match` — evaluate `Source` against sink `IpIsLocal`/trusted-proxy; keep `Source` on the public `EndpointPolicyMatch`/rule shape the resolver returns)
- Modify: default options seed (a health rule: `Path` in catalog + `Source=internal` → allow; overridable, not hard-coded ahead of resolution)
- Test: `src/Mostlylucid.BotDetection.Test/EndpointPolicies/EndpointPolicySourceMatcherTests.cs`

**Interfaces:** Produces `EndpointPolicyRule.Source`; resolver matches `internal` only when the source reads local/trusted (via sink `ReadBoolHint`, NOT MergedSignals).

- [ ] **Step 1: Failing tests** — rule `{Path:/health*, Source:internal, Action:allow}` matches a loopback request, does NOT match a public-source request.
- [ ] **Step 2: Run — FAIL.**
- [ ] **Step 3: Implement** the `Source` field + matcher (sink-read) + the default overridable health rule.
- [ ] **Step 4: Run — PASS.**
- [ ] **Step 5: Ping feature** — `inbox/feature-follow-up-endpointpolicy-source-shape.md`: the `EndpointPolicyRule` shape changed (added `Source`); update the topology doc references + confirm the per-domain resolver returns it.
- [ ] **Step 6: Commit** `feat(policy): source matcher on EndpointPolicyRule + default health policy`

---

### Task 6: "Health Probe" naming

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Services/FingerprintNameComposer.cs` (`Compose`/`ComposeFresh` `:78-160` — before Priority-1 claim extraction, if `health_endpoint` + expected-source-probe, return `"Health Probe"`; survives hysteresis as a non-fallback name)
- Test: `src/Mostlylucid.BotDetection.Test/Services/FingerprintNameComposerHealthProbeTests.cs`

**Interfaces:** Consumes `SignalKeys.HealthEndpoint` from the composed signals dict; produces the constant name `"Health Probe"`.

- [ ] **Step 1: Failing test** — signals with `health_endpoint`+local yield `Compose(...) == "Health Probe"`.
- [ ] **Step 2: Run — FAIL.**
- [ ] **Step 3: Implement** the short-circuit at the top of `ComposeFresh` (add a `HealthProbeName` const).
- [ ] **Step 4: Run — PASS.**
- [ ] **Step 5: Commit** `feat(health): name health probes "Health Probe"`

---

### Task 7: Acceptance + regression (feature owns end-to-end)

- [ ] **Step 1:** Integration test — loopback/Docker `curl -f /health` → 200 `Internal`/"Health Probe"; `kube-probe` UA → 200 Internal.
- [ ] **Step 2:** Shape-guard integration — browser-shaped `/health` from trusted IP → NOT auto-allowed.
- [ ] **Step 3:** Stat-exclusion — Health Probe (Internal) excluded from dashboard widget totals (ties #34).
- [ ] **Step 4:** Rebuild the Console + run `src/Mostlylucid.BotDetection.Console/verify-aot.sh` on an AOT publish → check #7 (`/health` healthy) green (was 7/8 → 8/8). Full suite green.
- [ ] **Step 5:** Hand off to feature for end-to-end verification (they own it); ping with the SHA range.
- [ ] **Step 6: Commit** any test-only additions `test(health): acceptance + verify-aot regression for health-endpoint Part 1`

---

## Self-Review

- **Spec coverage:** 2a→Task1; recognizer/catalog→Task2; shape+source→Task3; recon/threat_score→Task4; Source matcher+default→Task5; naming→Task6; acceptance (shape-guard, stat-exclusion, infra regress, verify-aot)→Task7. Parts 2/3 explicitly out of scope (tasks #12/#13). Covered.
- **Placeholder scan:** the `TestLedgerFactory.CurlLikeBot()` helper in Task 1 is the one build-it-if-absent hook (called out inline, not a silent TODO). Grep sites (`intent.threat_score` in Task 4) are exact-locate directions, resolved at implementation with a named target (`ProjectHoneypotAtom` magnitude).
- **Type consistency:** `SignalKeys.HealthEndpoint` / `HealthEndpointRecon`, `HealthEndpointCatalog.IsHealthPath`, `ProbeShapeClassifier.IsProbeShape`, `EndpointPolicyRule.Source`, `ReadBool(...)` local — used consistently across tasks.
