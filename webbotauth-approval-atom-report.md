# WebBotAuthApprovalAtom - Implementation Report

## Status: DONE

## Files Created / Modified

### New files
- `src/Mostlylucid.BotDetection/Auth/WebBotAuthOptions.cs` — `WebBotAuthOptions` (slot for future `ReverifyEveryNRequests`) + `WebBotAuthCachedVerdict` record (4 public-metadata fields only: `KeyId`, `Verdict`, `SubjectName`, `Algorithm`).
- `src/Mostlylucid.BotDetection/Orchestration/Atoms/WebBotAuthApprovalAtom.cs` — the atom itself. Priority 23; inert when no WBA headers.
- `src/Mostlylucid.BotDetection.Test/Orchestration/Atoms/WebBotAuthApprovalAtomTests.cs` — 7 unit tests (all passing).

### Modified files
- `src/Mostlylucid.BotDetection/Orchestration/Sessions/SessionAggregate.cs` — added `WebBotAuthVerdict { get; init; }` (nullable, `WebBotAuthCachedVerdict?`).
- `src/Mostlylucid.BotDetection/Orchestration/Sessions/SessionStore.cs` — added `SetWebBotAuthVerdict(siteId, fingerprintId, verdict)`. Creates a zero-sample stub aggregate when no aggregate exists; existing aggregates updated via `with { WebBotAuthVerdict = verdict }`. Does NOT raise on `Changes` (verdict update is not a behavioral shift).
- `src/Mostlylucid.BotDetection/Models/DetectionContext.cs` — added 6 locked C3 signal keys. `VerifiedBotName` already existed at `"verifiedbot.name"`, so the new identity-namespace constant is named `WbaVerifiedBotName` to avoid duplication; signal string value is `"identity.verified_bot_name"` as specified.
- `src/Mostlylucid.BotDetection/Orchestration/Atoms/BotDetectionOrchestrator.cs` — registered `WebBotAuthApprovalAtom` at Priority 23.
- `src/Mostlylucid.BotDetection/Modules/BotDetectionModule.cs` — added `AddOptions<WebBotAuthOptions>().BindConfiguration("BotDetection:WebBotAuth")`.

## Design discrepancy resolved

The brief spec says `SessionStore.TryGet(sessionId)` (single param). The actual method is `TryGet(string siteId, string fingerprintId)`. Resolved as follows:
- `fingerprintId` = `sink.ReadHint(SignalKeys.PrimarySignature)`
- `siteId` = `HttpContext.Request.Host.Host`, fallback `"default"` (FOSS single-tenant)

## Archetype seed

**Shipped.** `IdentityArchetypeRegistry.NudgeArchetype(archetypeId, vector, weight)` was added as the correct per-verification surface (rather than misusing the batch `IngestWellKnownBots` catalog-ingestion path). On a `TokenOutcome.Valid` result with a resolved subject name and an available identity vector, the atom nudges the `verified-{SubjectName}` centroid toward the current request's identity vector via bounded EMA. Fail-closed (unknown archetype = no-op) and no-clobber (bounded weight, never a hard replace) are enforced inside `NudgeArchetype`. Covered by `NudgeArchetypeTests` plus the archetype-nudge tests in `WebBotAuthApprovalAtomTests`.

## Test results

```
dotnet test --filter "FullyQualifiedName~WebBotAuth|FullyQualifiedName~Auth"
Passed:    97, Failed: 0, Total: 97
```

All 7 new tests + 90 pre-existing Auth tests pass.

## Build

```
dotnet build src/Mostlylucid.BotDetection -c Release
0 Error(s), 0 Warning(s)
```