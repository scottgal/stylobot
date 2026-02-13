# Test Status - Bot Detection

## Document Status
- Status: Historical/implementation note kept for engineering context.
- Canonical docs to use first: `docs/README.md`, `QUICKSTART.md`, `DOCKER_SETUP.md`.
- Website-friendly docs: `mostlylucid.stylobot.website/src/Stylobot.Website/Docs/`.


## Summary

The bot detection codebase has undergone significant architectural changes. Many tests require updates to match the new
API.

## Fixed Tests

### ✅ Detector Result API (Completed)

- Changed `result.BotProbability` → `result.Confidence`
- Changed `result.Reasons[0]` → `result.Reasons` collection checks
- **Fixed in**: All detector tests

### ✅ SecurityToolDetector Constructor (Completed)

- Added required `IOptions<BotDetectionOptions>` parameter
- Added required `IBotListFetcher` parameter
- **Fixed in**: SecurityToolDetectorTests.cs

## Tests Requiring Updates

### ⚠️ Detector Constructor Parameters

Several detectors now require additional constructor parameters:

**ClientSideDetectorTests.cs**

- Missing: `IOptions<BotDetectionOptions>`, `IBrowserFingerprintStore`
- Error: `CS7036`

**VersionAgeDetectorTests.cs**

- Missing: `IOptions<BotDetectionOptions>`, `IBrowserVersionService`
- Error: `CS7036`

### ⚠️ Signal Architecture Changes

**SignatureResponseCoordinatorTests.cs**

- Missing types: `BehavioralLane`, `SpectralLane`, `ReputationLane`, `OperationCompleteSignal`
- These types were removed or renamed in the new architecture
- **Count**: ~10 errors

**SignatureEscalatorAtomTests.cs**

- Missing types: `SignalPatternMatcher`, `EscalationRule`, `EscalatorConfig`
- These types were removed in favor of simpler escalation logic
- **Count**: ~15 errors

**ResponseAnalysisContextTests.cs**

- `ResponseSignal` API changed:
    - Removed: `Headers`, `ResponseTimeMs`
    - Added required: `ResponseBytes`, `Path`, `Method`
    - `ResponseBodySummary` requires: `IsPresent`, `Length`
- **Count**: ~8 errors

## Recommended Actions

### Quick Fix (Immediate)

1. Add `[Fact(Skip = "Needs update for new architecture")]` to failing tests
2. Document required changes in test comments
3. Keep passing tests running

### Proper Fix (Medium Term)

1. Update detector test constructors with proper mock dependencies
2. Rewrite signal-based tests for new architecture:
    - Use current signal types from `Mostlylucid.BotDetection.Orchestration`
    - Update to match `BlackboardState` signal model
    - Remove references to deprecated lane-based architecture
3. Update response signal tests for new API

## Architecture Changes Summary

### Old → New

**Detector Results:**

- `BotProbability` → `Confidence`
- `Reasons[0]` (string) → `Reasons` (List<DetectionReason>)

**Signals:**

- Lane-based (`BehavioralLane`, `SpectralLane`) → Blackboard-based
- `OperationCompleteSignal` → Removed (use blackboard state)
- `SignalPatternMatcher` → Simplified pattern matching

**Escalation:**

- `EscalationRule`, `EscalatorConfig` → Simplified escalation via `SignatureEscalatorAtom`

**Response Analysis:**

- Complex signal structure → Simplified required properties
- Explicit header/timing → Body-focused analysis

## Current Test Count

- **Total Tests**: ~200
- **Passing**: ~150 (75%)
- **Failing (Architecture)**: ~40 (20%)
- **Failing (Dependencies)**: ~10 (5%)

## Priority

1. **High**: Fix detector constructor dependencies (quick win)
2. **Medium**: Update response signal tests
3. **Low**: Rewrite orchestration tests (major refactor needed)

## Notes

- Integration tests may still pass (they use full DI container)
- Unit tests are most affected (direct constructor calls)
- Consider adding integration test coverage for new architecture
