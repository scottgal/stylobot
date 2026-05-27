# SessionStore must populate on every request — implementation plan

**Goal:** Eliminate the home-card "Calibrating fingerprint" forever-loop for typical human visitors by ensuring the in-process `SessionStore` ring accumulates a request entry on EVERY detection, not only when the orchestrator runs all the way to priority 30.

**Root cause:** Today the only writer to `SessionStore.RecordRequestAsync` is `SessionVectorContributor` at priority 30. The orchestrator quorum-exits before that priority for clear-human traffic (FastPathReputation fast-allow at priority 3 short-circuits the wave loop). The SessionStore stays empty → `SessionAtomizerService` finds nothing to finalise → `SignatureAggregate.LatestSessionVector` is never written → the home card calibrates indefinitely.

**Architecture (the fix):** Split `SessionVectorContributor` into two contributors with one clear concern each.

1. **`SessionRequestRecorderContributor`** (NEW) — priority 2. Records the request into `SessionStore` and that is it. No analysis, no contributions beyond a neutral marker. Runs in Wave 0/1 before any early-exit point. Always fires when `PrimarySignature` exists.
2. **`SessionVectorContributor`** (existing, simplified) — priority 30. Drops the `RecordRequestAsync` call. Just reads from the now-populated SessionStore for HNSW void detection, velocity, etc.

Once the recorder runs unconditionally, `SessionAtomizerService` sees the accumulated requests at its 2-minute tick, finalises sessions ≥ 3 requests, writes the resulting vector through `ISignatureVectorSink` (already wired in the earlier plan), and the home card polygon appears.

**Tech stack:** Existing — same orchestrator, same trigger system, same `SessionStore`, same `RequestMarkovClassifier`, same `BotDetectionJsonSerializerContext`.

---

## Anti-goals

- Do **not** record from `DetectionBroadcastMiddleware` post-orchestrator. The middleware lives in `Mostlylucid.BotDetection.UI` and doesn't have access to `BlackboardState` / `RequestMarkovClassifier` without code that would duplicate orchestrator logic. The recorder must stay inside the orchestrator, just at an earlier priority.
- Do **not** change `RequestMarkovClassifier.Classify(BlackboardState)`. The recorder consumes it exactly as the existing contributor does.
- Do **not** touch the early-exit behaviour. The fix is "run the recorder before early-exit can fire," not "disable early-exit."
- Do **not** introduce a new SessionStore implementation, a new background service, a new cache, or a new persistence path. The plumbing the previous plan already wired up (sink, atomizer write-through, cache field) is the read/write path. This plan only fixes the "who fills SessionStore" gap.

---

## What the orchestrator's priority numbers actually mean (verified)

- `SignatureContributor` priority 1 — writes `SignalKeys.PrimarySignature` during Wave 0.
- `BehavioralWaveformContributor` priority 3.
- `FastPathReputationContributor` priority 3 — this is where the early-exit fires for known-good IPs.
- `FingerprintMatchContributor` priority 6.
- `SessionVectorContributor` priority 30.

Early-exit triggers the wave loop break at the END of a wave (`BlackboardOrchestrator.cs:599 if (earlyExitTriggered) break;`). Contributors WITHIN the wave all execute. So a recorder at priority 2 with `SignalExistsTrigger(SignalKeys.PrimarySignature)` is gated until SignatureContributor completes, then runs in the same wave 0/1 boundary, and is guaranteed to fire before any wave-end early-exit check.

Priority 2 is the right slot: after the signal is written (priority 1), before the early-exit-prone contributors at priority 3+.

---

## File structure

**Create:**
- `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionRequestRecorderContributor.cs` — the new always-runs recorder. ~50 lines.
- `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/sessionrequestrecorder.detector.yaml` — manifest for priority + signal triggers. Mirrors the smallest existing manifest as a template.

**Modify:**
- `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs` — remove the `_sessionStore.RecordRequestAsync` call at line 137. Keep everything else.
- `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` — register the new contributor with DI alongside the existing `SessionVectorContributor` registration at line 834.

**No deletions.**

---

## Task 1: Create `SessionRequestRecorderContributor`

**Step 1.1 — manifest (`sessionrequestrecorder.detector.yaml`):**

```yaml
# Always-runs session-request recorder. Splits the SessionStore write side
# out of SessionVectorContributor (priority 30) so quorum-exit at priority 3
# doesn't leave SessionStore empty for clear humans. The vector analyzer
# stays at priority 30 -- this recorder is purely a request ingest.
name: SessionRequestRecorderContributor
priority: 2
enabled: true
description: >
  Records the current request into the in-process SessionStore ring on
  every detection. Reads SignalKeys.PrimarySignature, classifies the
  request via RequestMarkovClassifier, builds a SessionRequest, and
  writes through SessionStore.RecordRequestAsync. Returns NeutralContribution.

triggers:
  requires:
    - signal: signature.primary
      description: PrimarySignature is set by SignatureContributor at priority 1.

# Single tunable. No magic numbers in code.
defaults:
  parameters:
    skip_when_response_starts_with: []
```

**Step 1.2 — implementation:**

```csharp
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Priority-2 SessionStore writer. Splits the "record this request"
///     concern out of <see cref="SessionVectorContributor"/> (priority 30)
///     so the SessionStore ring populates on every detection regardless
///     of whether the orchestrator quorum-exits before priority 30. This
///     is the only writer that lets <see cref="Mostlylucid.BotDetection.Services.SessionAtomizerService"/>
///     find live sessions to finalise on its 2-minute tick; without it,
///     the dashboard's per-signature <c>LatestSessionVector</c> stays
///     null forever for clear-human signatures, and the home-card radar
///     calibrates indefinitely.
/// </summary>
public sealed class SessionRequestRecorderContributor : ConfiguredContributorBase
{
    private readonly ILogger<SessionRequestRecorderContributor> _logger;
    private readonly SessionStore _sessionStore;

    public SessionRequestRecorderContributor(
        ILogger<SessionRequestRecorderContributor> logger,
        IDetectorConfigProvider configProvider,
        SessionStore sessionStore)
        : base(configProvider)
    {
        _logger = logger;
        _sessionStore = sessionStore;
    }

    public override string Name => "SessionRequestRecorder";
    public override int Priority => 2;

    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        new SignalExistsTrigger(SignalKeys.PrimarySignature)
    ];

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var signature = state.GetSignal<string>(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature))
            return [NeutralContribution("No signature to record")];

        try
        {
            var requestState = RequestMarkovClassifier.Classify(state);
            var statusCode = state.HttpContext.Response.StatusCode;
            var path = SessionVectorContributor.TemplatizePath(state.HttpContext.Request.Path.Value ?? "/");

            var sessionRequest = new SessionRequest(
                requestState,
                DateTimeOffset.UtcNow,
                path,
                statusCode > 0 ? statusCode : 200);

            var fpContext = SessionVectorContributor.BuildFingerprintContext(state);

            // Fire and await: RecordRequestAsync is cheap (ring-buffer push +
            // gap-check). SessionVectorContributor already awaited the same
            // call. We don't need the returned completed-snapshot here --
            // SessionPersistenceService is independently subscribed to the
            // SessionFinalized event for the durability path.
            await _sessionStore.RecordRequestAsync(signature, sessionRequest, fpContext);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to record session request for {Signature}", signature);
        }

        return [NeutralContribution("Request recorded to session ring")];
    }
}
```

**Step 1.3 — expose `TemplatizePath` and `BuildFingerprintContext` from `SessionVectorContributor`:**

Currently those helpers are private. The recorder uses identical logic, so we change visibility from `private` to `internal static` (or move them to a shared static helper class in the same namespace). Same code, no duplication.

```csharp
// In SessionVectorContributor.cs -- change:
private static string TemplatizePath(string path) { ... }
private FingerprintContext BuildFingerprintContext(BlackboardState state) { ... }

// To:
internal static string TemplatizePath(string path) { ... }
internal static FingerprintContext BuildFingerprintContext(BlackboardState state) { ... }
```

Note: `BuildFingerprintContext` may use instance fields. If it does, hoist its body into a static method or pass dependencies explicitly. Verify and adjust during the task.

**Step 1.4 — DI registration:**

`src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs:834` already registers `SessionVectorContributor`. Add immediately above or below:

```csharp
services.AddSingleton<IContributingDetector, SessionRequestRecorderContributor>();
```

**Step 1.5 — commit:**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionRequestRecorderContributor.cs \
        src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/sessionrequestrecorder.detector.yaml \
        src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs \
        src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(orchestrator): always-runs SessionRequestRecorderContributor at priority 2"
```

---

## Task 2: Remove the duplicate write from `SessionVectorContributor`

**Step 2.1 — remove the `RecordRequestAsync` call:**

In `SessionVectorContributor.ContributeAsync`, find the existing line:

```csharp
var completedSession = await _sessionStore.RecordRequestAsync(signature, sessionRequest, fpContext);
```

Delete it. Also delete the `sessionRequest` and `fpContext` local builds if they're no longer used. The contributor's subsequent reads (`_sessionStore.GetCurrentSession(signature)`, `_sessionStore.GetHistory(signature)`) stay -- they see the data the recorder already pushed in Wave 0/1.

**Rationale comment to leave in the file (replace any existing one):**

```csharp
// SessionStore is now populated unconditionally by
// SessionRequestRecorderContributor at priority 2 -- this contributor
// only READS for analysis. The orchestrator can quorum-exit between
// priority 2 and 30; the recorder still fires, so the ring is hot for
// SessionAtomizerService even when this contributor never runs.
```

**Step 2.2 — commit:**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs
git commit -m "refactor(svc): read-only -- recorder split out to priority 2"
```

---

## Task 3: Verify the orchestrator dispatches at priority 2

**Step 3.1 — build:**

```bash
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj -c Debug --nologo
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj -c Debug --nologo
```

Expected: 0 errors. The new recorder slots in with the same `ConfiguredContributorBase` shape every other contributor uses.

**Step 3.2 — local existing test sanity:**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --nologo \
  --filter "FullyQualifiedName~SessionVector|FullyQualifiedName~SessionStore|FullyQualifiedName~Orchestrator"
```

Expected: all pass. The recorder doesn't change semantics for any consumer; the moved record call has the same arguments and the same `await` shape.

**Step 3.3 — deploy to staging, drive a fresh visitor, verify:**

1. Maxo build (`build-gateway.ps1`), force-recreate `stylobot-test-website`.
2. From chrome-devtools: navigate to `https://staging.stylobot.net/?fresh=...`, make 4-5 page hits to push the live session past `SessionMinRequests=3`, wait ≤ 2 minutes (one `SessionAtomizerService` tick).
3. Reload `/`. Assert `[data-sb-widget="your-detection"]` contains a `<polygon fill="...">` element (not the "Calibrating" placeholder).
4. Click through to `/dashboard/signature/{primarySig}`. Assert the API at `/dashboard/api/sessions/signature/{primarySig}` returns a row with `id: "current"` AND its `clockAxes` matches the home polygon's projected magnitudes within ε=0.001 (invert via `x = 50 + cos(angle) * 35 * m`).

PASS criterion: same polygon home vs detail, no fallback ladder.

---

## Configurable settings

None new. All thresholds (atomizer min-requests, session gap, run-interval) stay on existing `RetentionOptions`.

---

## Self-review

1. **Spec coverage:** every cause of the empty SessionStore is addressed. The recorder fires before the only known early-exit point (FastPathReputation at priority 3). It uses the same classification/fingerprint helpers `SessionVectorContributor` already uses. No call sites of the moved write need updating (the contributor was the only caller).
2. **Placeholder scan:** no TBDs. The recorder's signal trigger is concrete (`SignalExistsTrigger(SignalKeys.PrimarySignature)`); the manifest is concrete.
3. **Anti-goals honoured:** no broadcast-middleware write path, no orchestrator semantics change, no new cache or background service.
4. **Type consistency:** `SessionRequest`, `FingerprintContext`, `RequestMarkovClassifier`, and `SessionStore.RecordRequestAsync` are referenced with the exact signatures used by `SessionVectorContributor` today; no drift.
5. **Risk:** if `BuildFingerprintContext` reads instance fields, hoisting it to `internal static` may be lossy. Mitigation in Task 1.3: verify the helper has no `this`-dependencies; if it does, the recorder takes those dependencies via DI instead.

---

## Execution

User rule: commit on main, no branching. Two commits land sequentially: new recorder + manifest + DI → contributor cleanup. Maxo build kicks once, single deploy to staging, interaction-test gate. Total touched code: one new file (~50 lines), one new yaml (~15 lines), three small edits to existing files (visibility change, deleted call, DI line).
