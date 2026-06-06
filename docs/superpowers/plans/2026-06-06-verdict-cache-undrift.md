# Verdict Cache Un-Drift

> The fingerprint row IS the verdict cache. There is no parallel system.

**Branch:** `fix/verdict-cache-undrift` off `origin/main` @ `7a8d1439`. Observability + BDF detour preserved on `observability-bdf-detour-wip`.

**Goal:** Close the staging perf regression by:
1. Making the fingerprint dict the single source of truth for cached verdicts
2. Updating the fingerprint dict on every detection (EWMA-blended)
3. Removing the parallel `_verdictByPrimarySig` cache and the `cached_score_updated_at IS NOT NULL` SQL gate that filters fresh fingerprints

**Architecture:** Per `docs/architecture/fingerprint-match.md:98-104` ("cached_bot_probability EWMA | every request, in-line"). Confirmed during drift analysis that the request-path write hook was specified but never implemented; `FingerprintDriftService` was a placeholder that only ever ended up writing the timestamp, not the score. `IdentityAiOpinionService` is the only request-time writer today, and only via the manual operator AI button.

**Tech stack:** .NET 10, existing `Mostlylucid.BotDetection.Identity` namespace, SQLite via existing `SqliteFingerprintStore` LFU pattern.

**Design constraints:**
- No new cache layer. ([[feedback_no_unbacked_imemorycache]])
- Reuse the existing `_fingerprintById` dict as the source of truth. The architecture's universal write-through pattern.
- No persisted state schema change in this slice. The existing `cached_*` columns are reused.
- FOSS-additive ([[feedback_foss_never_degraded]]).
- Verify with running test before commit ([[feedback_verify_before_checkin]]).

---

## File Structure

```
src/Mostlylucid.BotDetection/Identity/
  IFingerprintStore.cs              # MODIFY: add RecordVerdictAsync to the interface
  SqliteFingerprintStore.cs         # MODIFY: implement RecordVerdictAsync, delete GetCachedVerdictForSignatureAsync + _verdictByPrimarySig
  IdentityVerdictLookup.cs          # MODIFY: TryGetAsync reads dict-direct
  IdentityOptions.cs                # MODIFY: add VerdictEwmaAlpha option (default 0.3)

src/Mostlylucid.BotDetection/Orchestration/
  EphemeralDetectionOrchestrator.cs # MODIFY: call RecordVerdictAsync at end of detection
  BlackboardOrchestrator.cs         # MODIFY: same hook in this orchestrator

src/Mostlylucid.BotDetection.Test/Identity/
  VerdictCacheReadFromFingerprintDictTests.cs   # NEW: pins L1 lookup reads dict-direct
  RecordVerdictAsyncTests.cs                    # NEW: pins EWMA + dict-write + SQL durability
```

---

## Task 1: TDD red — pin "burst within 1s hits L1 verdict cache"

**File:** `src/Mostlylucid.BotDetection.Test/Identity/VerdictCacheReadFromFingerprintDictTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Identity;

public class VerdictCacheReadFromFingerprintDictTests
{
    // Builds a SqliteFingerprintStore over a temp directory and returns it initialised.
    private static async Task<SqliteFingerprintStore> NewStoreAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stylobot-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "fingerprints-test.db");
        var opts = Options.Create(new BotDetectionOptions
        {
            DatabasePath = dbPath,
            Identity = { Enabled = true }
        });
        var layout = IdentityVectorLayout.DefaultV1;
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance,
            opts,
            layout);
        await store.EnsureInitialisedAsync();
        return store;
    }

    [Fact]
    public async Task RecordVerdict_FollowedByLookup_ReturnsVerdictWithoutDriftServiceTick()
    {
        var store = await NewStoreAsync();
        var lookup = new IdentityVerdictLookup(
            NullLogger<IdentityVerdictLookup>.Instance,
            store,
            Options.Create(new BotDetectionOptions { Identity = { Enabled = true } }));

        // Allocate a fingerprint via the test seam (or InsertFingerprintAsync directly).
        var primarySig = "test-primary-sig-1";
        var fpId = "test-fp-1";
        await store.InsertFingerprintAsync(
            new Fingerprint
            {
                FingerprintId = fpId,
                Centroid = new float[IdentityVectorLayout.DefaultV1.Dimension],
                Weights = new float[IdentityVectorLayout.DefaultV1.Dimension],
                CreatedAt = DateTime.UtcNow,
                ObservationCount = 1,
                InferredClientType = "chrome-desktop"
            },
            primarySig,
            CancellationToken.None);

        // Lookup BEFORE any verdict is recorded → null (cached_score_updated_at is still null).
        var before = await lookup.TryGetAsync(primarySig);
        before.Should().BeNull("a freshly-allocated fingerprint has no cached verdict yet");

        // Record a verdict from the request path. After this, subsequent requests within
        // the same session must see the cached verdict on L1 lookup without waiting for
        // the background drift service to tick.
        await store.RecordVerdictAsync(fpId, botProbability: 0.12, riskBand: "Low", CancellationToken.None);

        // Burst arrival: another request for the same primarySig 1ms later.
        var burst = await lookup.TryGetAsync(primarySig);
        burst.Should().NotBeNull("the verdict written one millisecond ago must be visible to the next L1 lookup");
        burst!.BotProbability.Should().BeApproximately(0.12, 1e-6,
            "first-ever verdict write is a direct assignment, not an EWMA blend");
        burst.FingerprintId.Should().Be(fpId);
    }

    [Fact]
    public async Task RecordVerdict_TwiceWithDifferentProbabilities_ExposesEwmaBlend()
    {
        var store = await NewStoreAsync();
        var primarySig = "test-primary-sig-2";
        var fpId = "test-fp-2";
        await store.InsertFingerprintAsync(
            new Fingerprint
            {
                FingerprintId = fpId,
                Centroid = new float[IdentityVectorLayout.DefaultV1.Dimension],
                Weights = new float[IdentityVectorLayout.DefaultV1.Dimension],
                CreatedAt = DateTime.UtcNow,
                ObservationCount = 1,
                InferredClientType = "chrome-desktop"
            },
            primarySig,
            CancellationToken.None);

        // First write: direct assignment (no prior).
        await store.RecordVerdictAsync(fpId, 0.10, "Low", CancellationToken.None);
        // Second write: should EWMA-blend with the first, not overwrite.
        await store.RecordVerdictAsync(fpId, 0.90, "VeryHigh", CancellationToken.None);

        var lookup = new IdentityVerdictLookup(
            NullLogger<IdentityVerdictLookup>.Instance,
            store,
            Options.Create(new BotDetectionOptions { Identity = { Enabled = true } }));

        var verdict = await lookup.TryGetAsync(primarySig);
        verdict.Should().NotBeNull();
        verdict!.BotProbability.Should().BeInRange(0.10, 0.90,
            "EWMA blend must land between the two writes, not overwrite to the second value");
        verdict.BotProbability.Should().NotBe(0.90,
            "direct overwrite would write 0.90 verbatim; EWMA must dampen the swing");
    }
}
```

If `IdentityVectorLayout.DefaultV1` isn't directly accessible (it's private static), use whatever existing test-double pattern the other identity tests already use; grep `SqliteFingerprintStoreTests` for the convention before falling back to reflection.

- [ ] **Step 2: Run, confirm compile failure**

```bash
dotnet build src/Mostlylucid.BotDetection.Test 2>&1 | tail -10
```

Expected: compile failure on `RecordVerdictAsync` (method not found). Good.

- [ ] **Step 3: Commit (red)**

```
test(identity): pin verdict-cache reads come from the fingerprint dict

Two failing tests:
- RecordVerdict followed by Lookup within ms must return the verdict (no drift
  service tick required)
- Two consecutive writes EWMA-blend, not overwrite

Implementation follows.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

---

## Task 2: Implement `RecordVerdictAsync` with EWMA blend

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/IFingerprintStore.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/SqliteFingerprintStore.cs`

- [ ] **Step 1: Add `VerdictEwmaAlpha` to `IdentityEngineOptions`**

Find the `IdentityEngineOptions` (or nearest equivalent) record/class in `IdentityOptions.cs`. Add:

```csharp
/// <summary>
///     EWMA smoothing factor for the fingerprint's cached bot probability. Each
///     request-path verdict blends in at <c>blend = old * (1 - alpha) + new * alpha</c>.
///     Default 0.3 favours stability over single-request swings. Higher values track
///     the latest request more closely; lower values resist noise. The very first
///     write to a fingerprint is a direct assignment regardless of alpha.
/// </summary>
public double VerdictEwmaAlpha { get; set; } = 0.3;
```

- [ ] **Step 2: Add `RecordVerdictAsync` to the interface**

Open `IFingerprintStore.cs`. Add:

```csharp
/// <summary>
///     Record a request-path verdict against the fingerprint's cached score.
///     EWMA-blends with the existing cached value (or assigns directly if none),
///     writes through the in-memory dict so the next L1 verdict lookup sees the
///     new value immediately, and persists to SQLite for durability.
/// </summary>
Task RecordVerdictAsync(
    string fingerprintId,
    double botProbability,
    string? riskBand,
    CancellationToken ct = default);
```

- [ ] **Step 3: Implement on `SqliteFingerprintStore`**

In `SqliteFingerprintStore.cs`, immediately below `UpdateCachedVerdictAsync` (around line 989), add:

```csharp
public async Task RecordVerdictAsync(
    string fingerprintId,
    double botProbability,
    string? riskBand,
    CancellationToken ct = default)
{
    if (string.IsNullOrEmpty(fingerprintId)) return;

    var alpha = Math.Clamp(_engineOptions.VerdictEwmaAlpha, 0.0, 1.0);

    // Dict-authoritative write: update the in-memory fingerprint so the next L1
    // verdict lookup sees it immediately. If the fingerprint isn't in the dict
    // (cold path), load it from SQLite first so we have something to blend against.
    if (!_fingerprintById.TryGetValue(fingerprintId, out var existing))
    {
        existing = await GetFingerprintAsync(fingerprintId, ct);
        if (existing is null) return;
    }

    var prior = existing.CachedBotProbability;
    var blended = prior is null
        ? botProbability                              // first write: direct
        : prior.Value * (1.0 - alpha) + botProbability * alpha;

    var now = DateTime.UtcNow;
    var updated = existing with
    {
        CachedBotProbability  = blended,
        CachedRiskBand        = riskBand ?? existing.CachedRiskBand,
        CachedScoreUpdatedAt  = now
    };

    // Atomic replace in the dict. ConcurrentDictionary's indexer is the write-through
    // point; the dict is authoritative on the hot read path.
    _fingerprintById[fingerprintId] = updated;

    // Durability: persist to SQLite. Synchronous one-row UPDATE; profile if it
    // becomes a hot-path bottleneck and consider switching to WriteBehindLfuStore.
    await using var conn = new SqliteConnection(_connectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        UPDATE fingerprints
           SET cached_bot_probability  = @prob,
               cached_risk_band        = @band,
               cached_score_updated_at = @ts
         WHERE fingerprint_id = @id
        """;
    cmd.Parameters.AddWithValue("@prob", blended);
    cmd.Parameters.AddWithValue("@band", (object?)updated.CachedRiskBand ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@ts", now.ToString("O"));
    cmd.Parameters.AddWithValue("@id", fingerprintId);
    await cmd.ExecuteNonQueryAsync(ct);
}
```

If `Fingerprint` is a positional record that doesn't support `with { CachedBotProbability = ... }` cleanly (e.g., property is not init-only), check the record definition; init-only is the convention. If init-only is missing, prefer fixing that over building a new mutable shadow.

- [ ] **Step 4: Run the tests**

```bash
dotnet build mostlylucid.stylobot.sln
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~VerdictCacheReadFromFingerprintDictTests"
```

Required: both new tests pass.

Note: the first test calls `RecordVerdictAsync` and then `lookup.TryGetAsync`. The lookup still uses the OLD path (`GetCachedVerdictForSignatureAsync`) at this point; that path's SQL gate (`cached_score_updated_at IS NOT NULL`) is now satisfied because we just wrote it. So the test passes BEFORE Task 3 lands. Task 3 then switches the lookup to dict-direct without breaking these tests.

- [ ] **Step 5: Commit**

```
feat(identity): RecordVerdictAsync writes through fingerprint dict + SQLite

Per architecture spec (docs/architecture/fingerprint-match.md:98-104) the
request-path verdict must EWMA-update the matched fingerprint's row. Until
now only FingerprintDriftService (background tick) and IdentityAiOpinionService
(manual operator AI button) wrote these columns. New RecordVerdictAsync closes
the gap: dict-authoritative write so the next L1 lookup sees the update,
synchronous SQLite persistence for restart-survival.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

---

## Task 3: Collapse `_verdictByPrimarySig` into dict-direct reads

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityVerdictLookup.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/SqliteFingerprintStore.cs`

- [ ] **Step 1: Refactor `IdentityVerdictLookup.TryGetAsync` to read dict-direct**

Replace the body of `TryGetAsync`:

```csharp
public async Task<IdentityCachedVerdict?> TryGetAsync(string primarySignature, CancellationToken ct = default)
{
    if (!_enabled) return null;
    if (string.IsNullOrEmpty(primarySignature)) return null;

    try
    {
        // Two dict hits (LFU-bounded, write-through to SQLite):
        // primarySig -> fingerprint_id  (via _fingerprintIdByPrimarySig)
        // fingerprint_id -> Fingerprint (via _fingerprintById)
        // The fingerprint row carries cached_bot_probability / risk_band / updated_at
        // directly. No parallel verdict cache, no SQL gate.
        var fingerprintId = await _store.LookupFingerprintIdAsync(primarySignature, ct);
        if (fingerprintId is null) return null;

        var fp = await _store.GetFingerprintAsync(fingerprintId, ct);
        if (fp is null) return null;
        if (fp.CachedScoreUpdatedAt is null) return null; // not yet matured

        return new IdentityCachedVerdict(
            FingerprintId: fp.FingerprintId,
            BotProbability: fp.CachedBotProbability ?? 0.0,
            RiskBand: fp.CachedRiskBand,
            UpdatedAtUtc: fp.CachedScoreUpdatedAt.Value,
            ObservationCount: fp.ObservationCount,
            InferredClientType: fp.InferredClientType ?? string.Empty);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Identity verdict lookup failed for primary signature");
        return null;
    }
}
```

- [ ] **Step 2: Delete the parallel verdict cache**

In `SqliteFingerprintStore.cs`:

1. Delete the field declaration `private readonly ConcurrentDictionary<string, IdentityCachedVerdict> _verdictByPrimarySig = new(StringComparer.Ordinal);` (line 43-44).
2. Delete the `_verdictEpoch` field (line 55).
3. Delete `GetCachedVerdictForSignatureAsync` (lines 267-312). This is the SQL with the `cached_score_updated_at IS NOT NULL` gate. The interface contract is now served by `IdentityVerdictLookup.TryGetAsync` reading via `LookupFingerprintIdAsync` + `GetFingerprintAsync`.
4. In `InvalidateFingerprintCache` (line 59-78), delete the `_verdictByPrimarySig` scan loop (lines 67-77) — that whole block becomes unnecessary. Keep the `_fingerprintById.TryRemove` line.
5. In `UpdateCachedVerdictAsync` (line 989-1019), delete the `foreach (var kv in _verdictByPrimarySig)` invalidation scan at lines 1014-1018. The `_fingerprintById.TryRemove` at 1013 stays — though once `RecordVerdictAsync` is the only request-path writer, even that becomes irrelevant for the live system. Leave it; `UpdateCachedVerdictAsync` is now manual-operator-only.

Find every other reference to `_verdictByPrimarySig` and `GetCachedVerdictForSignatureAsync` in the file and delete them. Build will tell you if you missed one.

Also delete:
- The `VerdictCacheMaxEntries` constant (line 39) if it's no longer referenced. Grep first.

Update `IFingerprintStore` to remove `GetCachedVerdictForSignatureAsync` from the interface if it's declared there. Any other callers? Grep the solution for `GetCachedVerdictForSignatureAsync`. If the only caller was the old `IdentityVerdictLookup.TryGetAsync` (now refactored), the cleanup is safe.

- [ ] **Step 3: Build + run the Task 1 tests**

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | tail -5
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~VerdictCacheReadFromFingerprintDictTests"
```

Required:
- 0 errors
- Both tests STILL pass (the dict-direct read returns the same verdict the SQL gate did)

If any other test fails, it's most likely a test that constructed `IdentityCachedVerdict` directly and depended on the old `_verdictByPrimarySig` slot. Grep `_verdictByPrimarySig` across all tests to find them. Adapt those tests to the new dict-direct model rather than restoring the parallel cache.

- [ ] **Step 4: Commit**

```
refactor(identity): delete _verdictByPrimarySig parallel cache; read fingerprint dict directly

The fingerprint row is the verdict cache. The separate _verdictByPrimarySig
dict was a parallel system that duplicated state already in _fingerprintById,
required an invalidation scan on every fingerprint update, and was populated
only via a SQL query that filtered out fresh fingerprints (cached_score_updated_at
IS NOT NULL). Collapsed into the dict-direct read in IdentityVerdictLookup.

Closes the 5-second structural delay where same-visitor bursts within the
drift-service tick interval missed the L1 verdict cache and re-ran the full
pipeline.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

---

## Task 4: Wire orchestrator(s) to call `RecordVerdictAsync` post-detection

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/EphemeralDetectionOrchestrator.cs`
- Modify: `src/Mostlylucid.BotDetection/Orchestration/BlackboardOrchestrator.cs`

The drift analysis identified line 817 in `BlackboardOrchestrator.cs` as the canonical post-detection fan-out point (right next to `TryPersistRequest`). The same hook applies to `EphemeralDetectionOrchestrator`.

- [ ] **Step 1: Inject `IFingerprintStore` if not already present**

Both orchestrators have constructor signatures. Read their existing constructor parameters. If `IFingerprintStore` is not already injected, add it as an optional last parameter:

```csharp
public BlackboardOrchestrator(
    // ... existing parameters ...
    IFingerprintStore? fingerprintStore = null)
{
    // ... existing assignments ...
    _fingerprintStore = fingerprintStore;
}
```

Field declaration alongside the others. If the store is already injected (likely, since the orchestrator already calls it elsewhere for matcher operations), skip this and just reuse the existing field.

- [ ] **Step 2: Add the post-detection hook**

After the existing post-detection fan-out (around line 817 in BlackboardOrchestrator, equivalent line in Ephemeral), add:

```csharp
// Write the request-path verdict through to the fingerprint cache. Architecture
// spec: cached_bot_probability EWMA, every request, in-line. Closes the cache
// gap where same-visitor bursts within the drift-service tick interval missed
// the L1 verdict cache.
if (_fingerprintStore is not null && result.Signals is not null &&
    result.Signals.TryGetValue(SignalKeys.IdentityFingerprintId, out var fpIdObj) &&
    fpIdObj is string fpId && !string.IsNullOrEmpty(fpId))
{
    try
    {
        await _fingerprintStore.RecordVerdictAsync(
            fpId,
            result.BotProbability,
            result.RiskBand?.ToString(),
            cancellationToken);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "RecordVerdict failed for fingerprint {FpId}", fpId);
        // fail-closed: persistence failure must not abort the request
    }
}
```

Verify the actual signal key constant name (`SignalKeys.IdentityFingerprintId` or similar — read `DetectionContext.cs`). Verify `result.RiskBand` exists on `AggregatedEvidence` and has a `ToString` that produces the canonical band string.

Apply the same hook to whichever post-detection method `EphemeralDetectionOrchestrator` uses.

- [ ] **Step 3: Verify the staging-reproduction scenario passes**

```bash
dotnet build mostlylucid.stylobot.sln
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~VerdictCacheReadFromFingerprintDictTests"
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~Identity"
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~Identity|FullyQualifiedName~Orchestrator"
```

Expected: all green. No new failures.

- [ ] **Step 4: Commit**

```
feat(orchestrator): write request-path verdict through to fingerprint cache

Both orchestrators now call IFingerprintStore.RecordVerdictAsync at the post-
detection fan-out point (next to RequestPersistenceService, SignatureCoordinator,
country-tracking writes). Same-visitor bursts within the FingerprintDriftService
tick interval no longer miss the L1 verdict cache and re-run the full pipeline.

Closes the staging perf regression where requests 2-N of a single visitor's
burst ran 13-15ms each instead of the expected sub-ms L1 hit.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

---

## Task 5: Final sweep

- [ ] **Step 1: Full solution build + identity tests**

```bash
dotnet build mostlylucid.stylobot.sln
dotnet test mostlylucid.stylobot.sln --filter "Category!=Integration"
```

Required: zero new failures vs. the baseline on `origin/main` (pre-existing failures from staging-baseline stay; nothing this branch added).

- [ ] **Step 2: Manual smoke (the staging-repro scenario, locally)**

```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo &
DEMO=$!
sleep 6

# Burst: HTML + 4 CSS-like asset fetches from the same UA + IP, 50ms apart.
UA="Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"
for path in "/SignatureDemo" "/css/site.css" "/css/site.css?v=2" "/js/site.js" "/favicon.ico"; do
    time curl -sS -A "$UA" -o /dev/null -w "%{http_code} %{time_total}s\n" "http://localhost:5080$path"
done

kill $DEMO 2>/dev/null
```

Expected: request 1 is the slow one (full pipeline). Requests 2-5 land sub-ms (L1 verdict hit + WeightedCosine confirm). If any of 2-5 are slower than 1, the dict write-through isn't firing.

If the test passes, the regression is closed locally. Push to staging is the user's call.

- [ ] **Step 3: Self-review checklist**

Per [[feedback_verify_before_checkin]]:
- [ ] All four commits land cleanly with no merge issues
- [ ] No em dashes anywhere in the new code or comments
- [ ] No new `IMemoryCache`, no new ConcurrentDictionary-as-cache  
- [ ] `_verdictByPrimarySig` and `GetCachedVerdictForSignatureAsync` are fully gone — `grep -rn` finds zero references
- [ ] `RecordVerdictAsync` is the only request-path writer of `cached_bot_probability`; `FingerprintDriftService.BumpCachedScoreCheckedAtAsync` continues as the drift-verification timestamp bumper

---

## Out of scope (documented gaps)

- **`FingerprintDriftService` repurposing.** Today it only bumps `cached_score_updated_at`. Its drift-verification role (re-check L1 hits against L2 cosine) was specified but the score update never landed. Out of scope for this slice; once `RecordVerdictAsync` is the request-path writer, the drift service's role narrows to "detect drift and override" which is a separate ticket.
- **EWMA alpha tuning.** Default 0.3 is a reasonable mid-point. Soak testing on staging will say whether to track-faster (0.5) or resist-noise-harder (0.1).
- **`WriteBehindLfuStore<TKey, TValue, TWriteOp>` migration.** `RecordVerdictAsync` currently does synchronous SQLite UPDATE. Profile under load before considering write-behind; the dict write is the hot read benefit, the SQL is restart-durability only.
- **Observability commits.** Preserved on `observability-bdf-detour-wip`. Resume after staging is stable.
- **BDF umbrella-centroid fix.** Preserved on the same branch. The Gaussian NLL + raw-channel work is sound but not on staging until calibration soak confirms it.

---

## Self-review

**Spec coverage:**
- "Make fingerprint dict the source of truth": Task 3 collapses the parallel cache.
- "EWMA write on every detection": Task 2 implements EWMA, Task 4 wires it into the orchestrator.
- "Don't lose observability/BDF": preserved on `observability-bdf-detour-wip`.

**Placeholder scan:** No TBDs. Every code block is concrete. Task 3 step 2 directs the implementer to grep before deleting (cleanup discipline) rather than enumerating every line.

**Type consistency:**
- `RecordVerdictAsync` signature is identical in Task 2 step 2 (interface), step 3 (impl), Task 4 step 2 (caller).
- `IdentityCachedVerdict` shape unchanged across Tasks 1-3.
- Signal key (`SignalKeys.IdentityFingerprintId`) is read once and reused.

**Out of scope honesty:** Listed deliberate gaps with reasons. EWMA tuning, write-behind migration, BDF/observability resumption all explicitly future-work.