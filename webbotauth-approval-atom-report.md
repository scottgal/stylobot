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

**TODO'd** with a note in the atom. The `IdentityArchetypeRegistry.IngestWellKnownBots` takes an enumerable of `(id, displayName, botType)` tuples and is designed for batch catalog ingestion, not per-verification nudges. Wiring a per-request nudge through that API would be a misuse. The correct surface is either a `NudgeArchetype(archetypeId, displayName, botType)` overload or seeding through the existing `WellKnownBotRefreshService` path once the bot name is confirmed. Left as adjudication item per the "Report BLOCKED" guidance.

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