# Persisted Fingerprint Display Name with Drift-Gated Updates

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every fingerprint always has a display name. Generated **once** on allocation from characterization (matched archetype name + UA family + OS + variance), persisted on the `Fingerprint` record, and read on every subsequent match. **Recomputed only when significant behavioural drift is detected** — never per-request, never gated on bot/human classification, never empty.

**Why this is required:** The 2026-05-16-naming-via-centroid-variance plan landed per-request derivation in the synthesizer. That produces names but they churn between requests when drift signals jitter. The contract the user articulated is: characterize → match → if no match generate; once generated, the name is stable until significant drift triggers an update. Per-request derivation also doesn't reach `evidence.PrimaryBotName` (which is what the response header / dashboard aggregate read), so the visible name surface is still empty for unknown clients today.

**Architecture:** Add `DisplayName` (and `DisplayNameUpdatedAt`) to the `Fingerprint` record, persisted in the `fingerprints` SQLite table. `FingerprintMatchContributor` computes the name **once** on the new-fingerprint branch using `FingerprintNameComposer.Compose(signals, matchedArchetype)`. Matched branches read the persisted `DisplayName` directly. A new "significant drift" path (drift score over `Match.SignificantDriftEpsilon`, higher than the per-request `DriftEpsilon` already in use) recomputes and persists the updated name. The matcher writes `identity.display_name` as a signal; `DetectionLedgerExtensions.ToAggregatedEvidence` reads that signal to fill `PrimaryBotName`, dropping the `isActuallyBot` gate that currently nulls it for humans.

**Tech Stack:** C# / .NET 10, SQLite (forward-only ALTER TABLE migration), xUnit + Moq.

**Critical invariants:**

1. **Name is stable per fingerprint.** Same fingerprint, 100 requests, same name. The variance term is part of the name when it's generated, not per-request decoration.
2. **Name update has a high bar.** Only fires when drift exceeds `SignificantDriftEpsilon` (default 0.20), which is 4× the existing per-request `DriftEpsilon` (0.05). Float noise must not move names.
3. **Two near-identical fingerprints can share the same display name.** This is correct — they're the same kind of client. The fingerprint identity (id, centroid) stays distinct; the display name is descriptive, not an identity merger.
4. **Bot classification ≠ display name.** "Bot" / "Human" is the verdict label (separate UI element). Display name is the derived identity ("Chrome on Windows", "python-requests", "curl"). Everyone has both. Removing the `isActuallyBot` gate on `PrimaryBotName` exposes the derived name regardless of verdict.
5. **`evidence.PrimaryBotType` stays gated.** That's a classification claim about what *kind* of bot — only valid when classified as bot. Keep the gate there.

---

## File Structure

### Modified
- `src/Mostlylucid.BotDetection/Identity/Fingerprint.cs` — add `DisplayName` (required string) + `DisplayNameUpdatedAt` (DateTime)
- `src/Mostlylucid.BotDetection/Identity/IdentitySchema.cs` — add columns to fingerprints CREATE + migration in `MigrateExistingTablesAsync`
- `src/Mostlylucid.BotDetection/Identity/SqliteFingerprintStore.cs` — insert/get/update mappers carry the new columns; add `UpdateDisplayNameAsync`
- `src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs` — add `Match.SignificantDriftEpsilon` (default 0.20)
- `src/Mostlylucid.BotDetection/Models/DetectionContext.cs` — add `IdentityDisplayName = "identity.display_name"` signal key
- `src/Mostlylucid.BotDetection/Services/DeterministicBotNameSynthesizer.cs` — keep its existing signal-based composition, but expose the static `GenerateName` as `internal static FingerprintNameComposer.Compose(signals)` so the matcher can call it
- `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintMatchContributor.cs` — compute on alloc, read on match, drift-gated recompute
- `src/Mostlylucid.BotDetection/Orchestration/DetectionLedgerExtensions.cs` — drop `isActuallyBot` gate on `PrimaryBotName`; prefer `identity.display_name` signal over `ledger.BotName` when both present
- `src/Mostlylucid.BotDetection/Endpoints/BdfReplayEndpoints.cs` — probe `IdentityDisplayName` in `SignalProbes` + surface on `BdfReplayActual`

### Created
- `src/Mostlylucid.BotDetection/Services/FingerprintNameComposer.cs` — pure-function name composition extracted from the synthesizer (same logic)
- `src/Mostlylucid.BotDetection.Test/Services/FingerprintNameComposerTests.cs` — characterization tests for the static composer
- `src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/Identity/FingerprintDisplayNameTests.cs` — alloc persists, match reads, drift-gate fires only at threshold

---

## Phase 1 — Storage: `Fingerprint.DisplayName` + schema migration

### Task 1.1: Add fields to `Fingerprint` record

**Files:** `src/Mostlylucid.BotDetection/Identity/Fingerprint.cs`

- [ ] **Step 1.1.1: Add `DisplayName` and `DisplayNameUpdatedAt`**

After `InferredTypeChangedAt`, add:

```csharp
    /// <summary>
    ///     Human-readable display name. Generated once at allocation via
    ///     <see cref="FingerprintNameComposer.Compose"/> from the matched archetype + UA
    ///     characterization. Updated only when drift exceeds
    ///     <c>Match.SignificantDriftEpsilon</c>. Never null — every fingerprint always has a
    ///     name, even if it's a short fingerprint-id prefix as the last-resort fallback.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    ///     UTC timestamp when <see cref="DisplayName"/> was last computed. Used by the
    ///     significant-drift path to age the name out gradually if behaviour shifts after
    ///     allocation, and as a freshness signal for the dashboard.
    /// </summary>
    public required DateTime DisplayNameUpdatedAt { get; init; }
```

- [ ] **Step 1.1.2: Update `FingerprintObservation` record similarly only if observations carry the name** — they do not (per-observation rows don't replicate naming). No change.

### Task 1.2: Schema migration

**Files:** `src/Mostlylucid.BotDetection/Identity/IdentitySchema.cs`

- [ ] **Step 1.2.1: Add columns to `CoreTables`**

In the fingerprints CREATE statement, after `ambiguity_persistence`:

```sql
        display_name                TEXT NOT NULL DEFAULT '',
        display_name_updated_at     TEXT NOT NULL DEFAULT ''
```

- [ ] **Step 1.2.2: Add ALTER TABLE migrations**

In `MigrateExistingTablesAsync`:

```csharp
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN display_name TEXT NOT NULL DEFAULT ''", ct);
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN display_name_updated_at TEXT NOT NULL DEFAULT ''", ct);
```

The `DEFAULT ''` keeps the column NOT NULL while allowing existing rows. The matcher backfills on next match (treats empty `DisplayName` as "needs compute").

### Task 1.3: Plumb columns through `SqliteFingerprintStore`

**Files:** `src/Mostlylucid.BotDetection/Identity/SqliteFingerprintStore.cs`

- [ ] **Step 1.3.1: Update `InsertFingerprintAsync`**

Add `display_name`, `display_name_updated_at` to the INSERT column list and parameters. The `Fingerprint` argument carries the values.

- [ ] **Step 1.3.2: Update all `GetFingerprintAsync` SELECTs**

Every projection that reads fingerprints needs to include the two new columns. There are ~5 of them in this file (search for `SELECT.*FROM fingerprints`). Update each SELECT to include `display_name, display_name_updated_at`, increment column indices in the reader.

- [ ] **Step 1.3.3: Add `UpdateDisplayNameAsync`**

```csharp
    /// <summary>
    ///     Updates a fingerprint's display name and timestamp. Called from the matcher's
    ///     significant-drift path (see <c>Match.SignificantDriftEpsilon</c>). Idempotent;
    ///     no-op when the row doesn't exist.
    /// </summary>
    public async Task UpdateDisplayNameAsync(
        string fingerprintId, string displayName, DateTime updatedAt, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE fingerprints
               SET display_name = @name, display_name_updated_at = @ts
             WHERE fingerprint_id = @id
            """;
        cmd.Parameters.AddWithValue("@name", displayName);
        cmd.Parameters.AddWithValue("@ts", updatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
```

- [ ] **Step 1.3.4: Run existing fingerprint store tests, fix mapper drift**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~Fingerprint"
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~Identity"
```

Expected: tests that build `new Fingerprint { ... }` will fail to compile because `DisplayName` / `DisplayNameUpdatedAt` are `required`. Add defaults (`DisplayName = "", DisplayNameUpdatedAt = DateTime.UtcNow`) to every test fixture.

- [ ] **Step 1.3.5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/Fingerprint.cs \
        src/Mostlylucid.BotDetection/Identity/IdentitySchema.cs \
        src/Mostlylucid.BotDetection/Identity/SqliteFingerprintStore.cs \
        src/Mostlylucid.BotDetection.Test/ \
        src/Mostlylucid.BotDetection.Orchestration.Tests/
git commit -m "feat(identity): persist DisplayName on Fingerprint record"
```

---

## Phase 2 — Pure name composer

### Task 2.1: Extract `FingerprintNameComposer` from the synthesizer

**Files:** `src/Mostlylucid.BotDetection/Services/FingerprintNameComposer.cs` (new), `src/Mostlylucid.BotDetection/Services/DeterministicBotNameSynthesizer.cs` (modified)

- [ ] **Step 2.1.1: Create the pure composer**

```csharp
namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Pure-function display-name composer. Same four-priority logic as
///     <see cref="DeterministicBotNameSynthesizer"/> but callable without an
///     <see cref="IBotNameSynthesizer"/> instance, so the matcher can compute names
///     synchronously during fingerprint allocation.
///
///     Priorities:
///       1. Known bot name from UA parsing (ua.bot_name).
///       2. Matched archetype name + drift variance (identity.archetype_name).
///       3. UA family + OS characterization (ua.family + user_agent.os).
///       4. Short fingerprint-id prefix as last-resort label (rare: only when no UA at all).
/// </summary>
internal static class FingerprintNameComposer
{
    /// <summary>
    ///     Compose a display name from request signals and an optional fingerprint id used
    ///     as the cold-state fallback. Never returns null or empty.
    /// </summary>
    public static string Compose(IReadOnlyDictionary<string, object?> signals, string? fingerprintId = null)
    {
        var country = GetString(signals, SignalKeys.GeoCountryCode);
        var signaturePrefix = GetString(signals, SignalKeys.PrimarySignature);

        // 1. Known bot name
        var botName = GetString(signals, SignalKeys.UserAgentBotName);
        if (!string.IsNullOrEmpty(botName) && botName != "unknown")
            return Unique(botName, signaturePrefix, country);

        // 2. Matched archetype name + variance
        var archetypeName = GetString(signals, SignalKeys.IdentityArchetypeName);
        if (!string.IsNullOrEmpty(archetypeName))
        {
            var variance = GetVarianceTerm(signals);
            var composed = string.IsNullOrEmpty(variance) ? archetypeName : $"{archetypeName} ({variance})";
            return Unique(composed, signaturePrefix, country);
        }

        // 3. UA family + OS characterization
        var family = GetString(signals, SignalKeys.UserAgentFamily);
        var os = GetString(signals, SignalKeys.UserAgentOs);
        if (!string.IsNullOrEmpty(family))
        {
            var composed = !string.IsNullOrEmpty(os) ? $"{family} on {os}" : family;
            var variance = GetVarianceTerm(signals);
            if (!string.IsNullOrEmpty(variance)) composed = $"{composed} ({variance})";
            return Unique(composed, signaturePrefix, country);
        }

        // 4. Last-resort: fingerprint id prefix
        if (!string.IsNullOrEmpty(fingerprintId))
            return Unique($"unknown {fingerprintId[..Math.Min(8, fingerprintId.Length)]}", signaturePrefix, country);

        return Unique("analysing", signaturePrefix, country);
    }

    // copies of Unique / GetVarianceTerm / GetString / GetDouble / GetBool from the synthesizer.
    // Single-source-of-truth lives here; the synthesizer delegates to this composer.
}
```

- [ ] **Step 2.1.2: Refactor `DeterministicBotNameSynthesizer` to delegate**

`GenerateName(signals)` becomes:

```csharp
    private static string GenerateName(IReadOnlyDictionary<string, object?> signals)
        => FingerprintNameComposer.Compose(signals);
```

Drop the duplicated helpers from the synthesizer.

- [ ] **Step 2.1.3: Add `FingerprintNameComposerTests`**

```csharp
public class FingerprintNameComposerTests
{
    [Fact]
    public void Compose_UsesArchetypeNamePlusOs_ForUnknownClient()
    {
        var signals = new Dictionary<string, object?>
        {
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "Windows",
            ["geo.country_code"] = "US"
        };
        var name = FingerprintNameComposer.Compose(signals);
        Assert.Contains("Chrome on Windows", name);
    }

    [Fact]
    public void Compose_FallsBackToFingerprintPrefix_WhenNoUa()
    {
        var signals = new Dictionary<string, object?>();
        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abc123def456");
        Assert.Contains("abc123de", name);
    }

    [Fact]
    public void Compose_NeverReturnsEmpty()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object?>());
        Assert.False(string.IsNullOrEmpty(name));
    }
}
```

- [ ] **Step 2.1.4: Run synthesizer tests, confirm pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~DeterministicBotNameTests|FullyQualifiedName~FingerprintNameComposerTests"
```

Expected: all green.

- [ ] **Step 2.1.5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/FingerprintNameComposer.cs \
        src/Mostlylucid.BotDetection/Services/DeterministicBotNameSynthesizer.cs \
        src/Mostlylucid.BotDetection.Test/Services/FingerprintNameComposerTests.cs
git commit -m "refactor(naming): extract FingerprintNameComposer as pure static helper"
```

---

## Phase 3 — Compute on alloc, read on match, drift-gated update

### Task 3.1: Add `Match.SignificantDriftEpsilon` option + `IdentityDisplayName` signal

**Files:** `src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs`, `src/Mostlylucid.BotDetection/Models/DetectionContext.cs`

- [ ] **Step 3.1.1: Add the option**

In `IdentityMatchOptions`:

```csharp
    /// <summary>
    ///     Drift threshold that triggers a display-name recompute + persist on a matched
    ///     fingerprint. ~4× DriftEpsilon — float noise must not move names. Default 0.20.
    /// </summary>
    public double SignificantDriftEpsilon { get; set; } = 0.20;
```

- [ ] **Step 3.1.2: Add the signal key**

In `SignalKeys.Identity` block:

```csharp
    /// <summary>string: the fingerprint's persisted display name (stable across requests, updated only on significant drift). Written by FingerprintMatchContributor on every match. The aggregator's PrimaryBotName reads this; the dashboard / response header display it.</summary>
    public const string IdentityDisplayName = "identity.display_name";
```

- [ ] **Step 3.1.3: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs \
        src/Mostlylucid.BotDetection/Models/DetectionContext.cs
git commit -m "feat(identity): SignificantDriftEpsilon option + display_name signal key"
```

### Task 3.2: Wire the matcher

**Files:** `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintMatchContributor.cs`

- [ ] **Step 3.2.1: Compute on allocation**

In the new-fingerprint branch (around line 256, where `newFp` is constructed):

After the existing `state.WriteSignal(SignalKeys.IdentityClientType, …)`, *before* `WriteArchetypeSignals`:

```csharp
        var nullableSignals = state.Signals.ToDictionary(s => s.Key, s => (object?)s.Value);
        var displayName = FingerprintNameComposer.Compose(nullableSignals, newId);
```

Set on the `newFp` record at construction:

```csharp
        var newFp = new Fingerprint
        {
            // ... existing fields ...
            DisplayName = displayName,
            DisplayNameUpdatedAt = now,
        };
```

After `state.WriteSignal(SignalKeys.IdentityClientType, ...)`:

```csharp
        state.WriteSignal(SignalKeys.IdentityDisplayName, displayName);
```

- [ ] **Step 3.2.2: Read on match**

In `EmitConfirmedSignals` (currently writes archetype name via `WriteArchetypeSignals`), add an `IdentityDisplayName` write **from the matched fingerprint's persisted name**:

```csharp
        if (!string.IsNullOrEmpty(matched.DisplayName))
            state.WriteSignal(SignalKeys.IdentityDisplayName, matched.DisplayName);
```

Edge case: empty `DisplayName` on a row migrated from before this change. Compose lazily and persist:

```csharp
        if (string.IsNullOrEmpty(matched.DisplayName))
        {
            var sigs = state.Signals.ToDictionary(s => s.Key, s => (object?)s.Value);
            var name = FingerprintNameComposer.Compose(sigs, matched.FingerprintId);
            state.WriteSignal(SignalKeys.IdentityDisplayName, name);
            // Fire-and-forget persist; don't block the request on the write.
            _ = _store.UpdateDisplayNameAsync(matched.FingerprintId, name, DateTime.UtcNow, CancellationToken.None);
        }
```

- [ ] **Step 3.2.3: Drift-gated recompute**

`WriteArchetypeSignals` already computes the top drift. Extend it: when `drift.Score > _options.Match.SignificantDriftEpsilon` AND the matched fingerprint has a `DisplayName`, recompute and persist:

```csharp
        if (drift is not null && drift.Value.Score > _options.Match.SignificantDriftEpsilon)
        {
            var sigs = state.Signals.ToDictionary(s => s.Key, s => (object?)s.Value);
            var newName = FingerprintNameComposer.Compose(sigs, /* fingerprintId here — need to thread it */);
            if (!string.Equals(newName, /* current name */, StringComparison.Ordinal))
            {
                state.WriteSignal(SignalKeys.IdentityDisplayName, newName);
                _ = _store.UpdateDisplayNameAsync(/* fingerprintId */, newName, DateTime.UtcNow, CancellationToken.None);
            }
        }
```

To thread the matched fingerprint's id + current name into `WriteArchetypeSignals`, change its signature:

```csharp
    private void WriteArchetypeSignals(
        BlackboardState state, float[] vector, IdentityArchetype? archetype,
        Fingerprint? matchedForDriftUpdate = null)
```

Pass `matched` from each `EmitConfirmedSignals` call site; pass `null` from the new-fingerprint branch (no drift update on alloc — name was just computed).

- [ ] **Step 3.2.4: Run BDF rig + identity tests**

```bash
dotnet build src/Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "Category=Integration&FullyQualifiedName~BdfReplayTests"
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~Identity"
```

Expected: BDF rig passes (additional probe added in Phase 5); identity tests pass.

- [ ] **Step 3.2.5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintMatchContributor.cs
git commit -m "feat(identity): compute display name on alloc, read on match, drift-gated update"
```

---

## Phase 4 — Aggregator reads `identity.display_name`

### Task 4.1: `DetectionLedgerExtensions.ToAggregatedEvidence`

**Files:** `src/Mostlylucid.BotDetection/Orchestration/DetectionLedgerExtensions.cs`

- [ ] **Step 4.1.1: Drop the `isActuallyBot` gate on `primaryBotName`**

Replace lines 63-65 with:

```csharp
        // PrimaryBotType stays gated — it's a classification claim ("this looks like a Scraper")
        // only meaningful when classified as bot.
        var isActuallyBot = botProbability >= 0.5;
        var primaryBotType = isActuallyBot ? ParseBotType(ledger.BotType) : null;

        // PrimaryBotName is NEVER gated. Every fingerprint always has a display name.
        // Priority: matcher-set identity.display_name (the persisted, drift-gated name) →
        // ledger.BotName (UA-derived) → null (only if neither path produced one).
        var displayNameFromSignal = preSignals.TryGetValue(SignalKeys.IdentityDisplayName, out var dnObj)
            ? dnObj as string : null;
        var primaryBotName = !string.IsNullOrEmpty(displayNameFromSignal)
            ? displayNameFromSignal
            : (isActuallyBot ? ledger.BotName : null);
```

- [ ] **Step 4.1.2: Same change in `CreateEarlyExitResult`**

Apply the equivalent change to the early-exit code path around line 156 (`primaryBotName = exitContrib.BotName;`):

```csharp
        var earlyDisplayName = earlySignals.TryGetValue(SignalKeys.IdentityDisplayName, out var earlyDnObj)
            ? earlyDnObj as string : null;
        var primaryBotName = !string.IsNullOrEmpty(earlyDisplayName)
            ? earlyDisplayName
            : exitContrib.BotName;
```

- [ ] **Step 4.1.3: Run all tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests
```

Expected: all unit tests pass. BDF integration green.

- [ ] **Step 4.1.4: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/DetectionLedgerExtensions.cs
git commit -m "feat(aggregator): PrimaryBotName reads identity.display_name; never gated on IsBot"
```

---

## Phase 5 — BDF probe + acceptance verification

### Task 5.1: Probe `IdentityDisplayName`

**Files:** `src/Mostlylucid.BotDetection/Endpoints/BdfReplayEndpoints.cs`, `src/Mostlylucid.BotDetection.Orchestration.Tests/Integration/BdfReplayTests.Integration.cs`

- [ ] **Step 5.1.1: Add probe + surface field**

In `BdfReplayEndpoints`:

```csharp
            var signalProbes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                // ... existing probes ...
                [Models.SignalKeys.IdentityDisplayName] = signals.ContainsKey(Models.SignalKeys.IdentityDisplayName)
            };
            // ...
            var identityDisplayName = signals.TryGetValue(Models.SignalKeys.IdentityDisplayName, out var dnObj)
                ? dnObj as string : null;
```

In `BdfReplayActual`:

```csharp
    public string? IdentityDisplayName { get; set; }
```

And in the construction below.

- [ ] **Step 5.1.2: Assert in `AssertSignalsFlowed`**

```csharp
        // Display name is the contract: every fingerprinted request has a non-empty name.
        if (last.Actual.IdentityFingerprintId is not null)
        {
            Assert.True(probes.TryGetValue(SignalKeys.IdentityDisplayName, out var hasName) && hasName,
                $"{scenarioName}: {SignalKeys.IdentityDisplayName} missing from ev.Signals — " +
                "FingerprintMatchContributor did not write the persisted display name");
            Assert.False(string.IsNullOrEmpty(last.Actual.IdentityDisplayName),
                $"{scenarioName}: identity.display_name was empty — the contract is that every " +
                "fingerprint always has a name");
        }
```

This assertion IS load-bearing — unlike the archetype-name probe (which can be absent when no archetype matches), `display_name` must always be present when there's a fingerprint. The composer's last-resort fallback (fingerprint id prefix) guarantees it.

- [ ] **Step 5.1.3: Run BDF rig**

```bash
dotnet build src/Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "Category=Integration&FullyQualifiedName~BdfReplayTests"
```

Expected: 17/17 pass.

- [ ] **Step 5.1.4: Commit**

```bash
git add src/Mostlylucid.BotDetection/Endpoints/BdfReplayEndpoints.cs \
        src/Mostlylucid.BotDetection.Orchestration.Tests/Integration/BdfReplayTests.Integration.cs
git commit -m "test(bdf): assert every fingerprint carries a non-empty display name"
```

### Task 5.2: Live smoke test

- [ ] **Step 5.2.1: Run the demo, hit it as Chrome + curl, confirm headers**

```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo &
# wait for ready
curl -sI -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36" http://localhost:5080/api/demo/open | grep StyloBot-BotName
curl -sI -A "curl/8.7.1" http://localhost:5080/api/demo/open | grep StyloBot-BotName
```

Expected output:
- Chrome: `X-StyloBot-BotName: Chrome on Windows (US:<sigprefix>)` (or similar — archetype name when matched, family+OS when not)
- curl: `X-StyloBot-BotName: curl (US:<sigprefix>)` (UA-derived priority)

Neither should be empty.

- [ ] **Step 5.2.2: Run the demo, hit the dashboard, confirm visitor rows show names**

Open `http://localhost:5080/_stylobot` and confirm every visitor row shows a derived name in the name column (not a bare signature hash, not "Human", not "Unknown").

---

## Acceptance criteria

1. **Every fingerprinted request has a non-empty `identity.display_name` signal.** Pinned by BDF rig assertion in Phase 5.
2. **Response header `X-StyloBot-BotName` is never empty** for any request that ran through the orchestrator. Verified by Phase 5 smoke test.
3. **Names are stable across requests for the same fingerprint.** Same fingerprint, 100 requests → same display name in 100 responses (modulo significant drift).
4. **Significant drift triggers an update.** If a fingerprint's drift score exceeds `Match.SignificantDriftEpsilon` (0.20), the name is recomputed and persisted; subsequent requests show the new name.
5. **No regression in the verdict pipeline.** `IsBot` / `BotProbability` / `RiskBand` continue to compute correctly; bot vs human classification is independent of naming.
6. **Schema migration is idempotent.** Existing `fingerprints.db` databases without the columns get them added on next start; rows have empty `DisplayName` initially, get backfilled on next match by the lazy-compose-and-persist path in `EmitConfirmedSignals`.

---

## Self-review

**Spec coverage:**
- "Fingerprints always have a name" → Phase 1+2+3 (persist on alloc, read on match, lazy backfill for migrated rows, last-resort id prefix fallback).
- "First time we see an unknown we have UA / platform / geo" → Composer priority 3 reads `ua.family`, `user_agent.os`, `geo.country_code`.
- "Characterize → match → if no match generate" → Composer priorities 1 (bot name from UA), 2 (archetype match), 3 (UA characterization generation), 4 (id fallback).
- "Names update only when significant behavioural drift detected" → `SignificantDriftEpsilon` gate in `WriteArchetypeSignals`. Default 0.20 (4× existing per-request `DriftEpsilon`).
- "Verdict label ≠ name" → Phase 4 drops `isActuallyBot` gate on `PrimaryBotName` but keeps it on `PrimaryBotType`.

**Placeholders:** Every step has the actual code or command. The "thread fingerprintId" comment in Phase 3.2.3 is a callout, not a placeholder — the immediately-following code block shows exactly how to thread it.

**Type consistency:** `DisplayName` (Fingerprint field, signal value, response header) is `string` throughout. `DisplayNameUpdatedAt` is `DateTime` everywhere. `SignalKeys.IdentityDisplayName = "identity.display_name"` is the single key used by writer (matcher) and reader (aggregator + probe).

**Open question for the worker:** the lazy-backfill path in 3.2.2 uses `_ = _store.UpdateDisplayNameAsync(...)` fire-and-forget. If you'd rather make it deterministic (await before returning), that's safer but adds latency to the first matched request for migrated rows. The fire-and-forget is consistent with how observations are recorded (`_store.RecordObservationAsync` calls aren't awaited either). Document the choice in the commit message.
