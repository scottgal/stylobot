# Encoder Raw-Channel for the Matcher

> Sub-plan to `2026-06-05-bdf-umbrella-centroid-fix.md` — fixes the deeper root cause the implementer surfaced after the Gaussian-NLL math landed.

**Goal:** Stop the matcher (`MaskedSimilarity`) comparing L2-normalized vectors. Add a raw (unnormalized) channel from the encoder to the matcher so per-dim diffs reflect actual signal values, not normalization-divergent positions on the unit hypersphere. Leave the normalized path untouched for cosine-based consumers.

**Why this instead of dropping L2 normalization entirely:**
- `BruteForceIdentityAnchorIndex.Cosine` is optimized for unit-length inputs (skips re-normalization).
- Persisted per-fingerprint centroids in SQLite are stored normalized; a wholesale change forces a schema migration we don't need to take right now.
- Path A (dual channel) is additive. Existing consumers keep working unchanged.

**Architecture:**
- `IdentityVectorEncoder.Encode` keeps returning the normalized vector.
- Add `IdentityVectorEncoder.EncodeRaw` returning the unnormalized vector for the same `rawValues` dict.
- `IdentityArchetype` gains a `CentroidRaw` property populated at compile time.
- `IdentityArchetypeRegistry.MaskedSimilarity` consumes raw values: takes a raw vector parameter and uses `archetype.CentroidRaw`.
- The matcher's caller (`FingerprintMatchContributor` / orchestrator) passes the raw vector instead of the normalized one to `FindNearest`.
- TDD: a test asserting that two raw vectors with identical values on a shared subset of dims produce the maximum-similarity score regardless of how many other dims are populated.

**Constraints:**
- Persisted state (per-fp centroids in SQLite) unchanged. Pass 2 cosine matching against stored fingerprints continues to use the normalized path.
- No UA-family allowlist (carrying constraint from parent plan).
- FOSS-additive ([[feedback_foss_never_degraded]]).

---

## File Structure

```
src/Mostlylucid.BotDetection/Identity/
  IdentityVectorLayout.cs                       # MODIFY: add EncodeRaw method to IdentityVectorEncoder
  IdentityArchetype.cs                          # MODIFY: add CentroidRaw property
  IdentityArchetypeRegistry.cs                  # MODIFY: Compile captures raw; MaskedSimilarity uses raw
  
src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/
  IdentityVectorContributor.cs                  # MODIFY: encode raw + write to signal

src/Mostlylucid.BotDetection/Models/
  DetectionContext.cs                           # MODIFY: add SignalKeys.IdentityVectorRaw constant

src/Mostlylucid.BotDetection/Identity/
  FingerprintMatchContributor.cs                # MODIFY: FindNearest call uses raw vector

src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/Identity/
  ArchetypeMatchScoringTests.cs                 # MODIFY: add 2 new tests for raw-channel parity
```

---

## Task 1: Add `EncodeRaw` to the encoder

**File:** `src/Mostlylucid.BotDetection/Identity/IdentityVectorLayout.cs`

- [ ] **Step 1: Read the existing Encode method**

Read lines around 185-270 of `IdentityVectorLayout.cs`. Confirm the structure:
- `public float[] Encode(IReadOnlyDictionary<string, object?> rawValues)` allocates `v = new float[_layout.Dimension]`
- Iterates slots, populates `v[slot.Offset...slot.Offset+slot.Width]`
- Updates `quality.dimension_presence_ratio` (lines ~259-261)
- Calls `L2Normalise(v)` as the last step (line ~268)
- Returns `v`

- [ ] **Step 2: Refactor to share the core encoding**

Extract the core encoding into a private helper `EncodeCore` that returns the unnormalized vector. Keep `Encode` as a wrapper that normalizes:

```csharp
public float[] Encode(IReadOnlyDictionary<string, object?> rawValues)
{
    var v = EncodeCore(rawValues);
    L2Normalise(v);
    return v;
}

/// <summary>
///     Encode without the terminal L2 normalization. Use when comparing raw signal magnitudes
///     between an observation and a centroid is more meaningful than comparing positions on
///     the unit hypersphere. Required for variance-aware scoring in IdentityArchetypeRegistry.
/// </summary>
public float[] EncodeRaw(IReadOnlyDictionary<string, object?> rawValues)
{
    return EncodeCore(rawValues);
}

private float[] EncodeCore(IReadOnlyDictionary<string, object?> rawValues)
{
    // body of the existing Encode method MINUS the final L2Normalise call
}
```

Move the entirety of the current `Encode` body (excluding the `L2Normalise(v)` line) into `EncodeCore`. Both public methods call into it.

- [ ] **Step 3: Build**

```bash
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
```

Expected: succeeds. No new warnings.

- [ ] **Step 4: Commit**

```
refactor(identity): extract EncodeCore + add EncodeRaw to IdentityVectorEncoder

Encode keeps the L2-normalized output for cosine consumers. EncodeRaw returns
the unnormalized vector for variance-aware scoring where raw signal magnitudes
matter more than unit-sphere positions. Both share EncodeCore so the encoding
logic stays single-sourced.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

(HEREDOC.)

---

## Task 2: Add `CentroidRaw` to `IdentityArchetype` and populate at compile time

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityArchetype.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityArchetypeRegistry.cs`

- [ ] **Step 1: Add the property**

In `IdentityArchetype.cs`, immediately after the existing `Centroid` property add:

```csharp
/// <summary>
///     The pre-L2-normalisation centroid built directly from the YAML's raw dimension values.
///     <see cref="Centroid"/> is unit-length for cosine matching; <c>CentroidRaw</c> preserves
///     the original magnitudes for variance-aware scoring.
///
///     Null on archetypes built before this property was added (backwards-compat); callers
///     fall back to <see cref="Centroid"/> when null. Always populated by
///     <c>IdentityArchetypeRegistry.Compile</c> from this commit forward.
/// </summary>
public float[]? CentroidRaw { get; init; }
```

- [ ] **Step 2: Populate at compile time**

In `IdentityArchetypeRegistry.cs`, find `Compile(IdentityArchetypeYaml dto)` (around line 245-279). The current pattern is:

```csharp
var rawValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
foreach (var (name, entry) in dto.Dimensions ?? new())
    rawValues[name] = entry.Value;
var centroid = _encoder.Encode(rawValues);
```

Add a parallel raw encode:

```csharp
var rawValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
foreach (var (name, entry) in dto.Dimensions ?? new())
    rawValues[name] = entry.Value;
var centroid = _encoder.Encode(rawValues);
var centroidRaw = _encoder.EncodeRaw(rawValues);
```

Then in the `new IdentityArchetype { ... }` object initializer below, add:

```csharp
CentroidRaw = centroidRaw,
```

next to the existing `Centroid = centroid,`.

- [ ] **Step 3: Build**

```bash
dotnet build mostlylucid.stylobot.sln
```

Expected: succeeds.

- [ ] **Step 4: Commit**

```
feat(identity): populate CentroidRaw alongside Centroid at compile time

Archetype now carries both: Centroid (L2-normalized, for cosine matching) and
CentroidRaw (pre-normalization, for variance-aware scoring). Pure additive,
no consumer change yet.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

---

## Task 3: TDD: add 2 failing parity tests

**File:** `src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/Identity/ArchetypeMatchScoringTests.cs`

- [ ] **Step 1: Read the existing test file end-to-end**

Find the `BuildArchetype` helpers added in commits `ef3b95f4` and `874392c4`. We'll add a similar helper that also passes raw centroid values. Also find `ScoreAgainst` and the `_encoder` field.

- [ ] **Step 2: Add a `BuildArchetypeWithRaw` helper**

At the bottom of the class:

```csharp
private static IdentityArchetype BuildArchetypeWithRaw(
    string id, float[] centroid, float[] centroidRaw, float[] mask, float[]? variance)
{
    return new IdentityArchetype
    {
        ArchetypeId = id,
        Name = id,
        Description = "",
        ArchetypeKind = "test",
        Centroid = centroid,
        CentroidRaw = centroidRaw,
        DimensionMask = mask,
        VarianceVector = variance
    };
}
```

Match the property names that exist on the real `IdentityArchetype` record (Name/Description may or may not be required; check by attempting build).

- [ ] **Step 3: Add the parity test that proves the bug exists**

```csharp
[Fact]
public void MaskedSimilarity_RawCentroidMatchesRawObservation_OnSubsetOfDims_ScoresHighRegardlessOfPopulationDensity()
{
    // The umbrella problem in encoder form: an archetype declares 3 dims; an observation
    // populates those 3 PLUS 10 unrelated dims. After L2 normalization, the 3 matched dims
    // land at different unit-sphere positions because the two vectors have different magnitudes.
    //
    // With CentroidRaw + raw observation, the diff on the 3 matched dims is exactly zero
    // and the score is high regardless of how many other dims the observation populated.

    var dim = _encoder.Layout.Dimension;

    var sharedSlots = new[] { 10, 11, 12 };
    var raw = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    // Populate matching dims via the existing encoder slot semantics. Use whatever raw value
    // type the slot expects (network booleans etc. — read the layout).
    // Replace these with actual slot names from IdentityVectorLayout.DefaultV1 — find three
    // boolean-encoded slots so the raw values are 0/1 floats.
    raw["network.is_datacenter"] = true;
    raw["network.is_vpn"] = true;
    raw["network.is_tor"] = true;

    var observationRaw = _encoder.EncodeRaw(raw);

    // Centroid encodes the same three slot values, nothing else.
    var centroid = _encoder.Encode(raw);
    var centroidRaw = _encoder.EncodeRaw(raw);

    var mask = new float[dim];
    foreach (var s in sharedSlots) mask[s] = 0.9f;

    var variance = new float[dim];
    Array.Fill(variance, 0.05f);

    var archetype = BuildArchetypeWithRaw("perfect-match", centroid, centroidRaw, mask, variance);

    // Now build an observation that ALSO populates 10 unrelated slots so its L2 norm differs.
    var rawExpanded = new Dictionary<string, object?>(raw, StringComparer.OrdinalIgnoreCase);
    // Add 10 more populated signals — pick string-encoded slots so they contribute to magnitude.
    rawExpanded["hdr.ua_family"] = "Chrome";
    rawExpanded["hdr.accept"] = "*/*";
    rawExpanded["hdr.accept_encoding_ordered"] = "gzip, deflate, br";
    rawExpanded["hdr.sec_ch_ua_mobile"] = false;
    rawExpanded["hdr.upgrade_insecure_requests"] = true;
    rawExpanded["network.country_code"] = "US";
    rawExpanded["network.asn"] = "AS15169";
    rawExpanded["hdr.dnt"] = true;
    rawExpanded["hdr.sec_fetch_pattern"] = 15;
    rawExpanded["session.path_diversity"] = 0.5;

    var observationRawExpanded = _encoder.EncodeRaw(rawExpanded);
    var observationNormalizedExpanded = _encoder.Encode(rawExpanded);

    // The bug we're fixing: under the old normalized path, scoring observationNormalizedExpanded
    // against archetype.Centroid produces a LOW score because the 3 matched dims now have
    // different post-normalization values.
    // The fix: scoring observationRawExpanded against archetype.CentroidRaw produces a HIGH
    // score because the 3 matched dims have IDENTICAL raw values.

    var rawScore = _registry.ScoreAgainstRaw(observationRawExpanded, archetype);
    var normalizedScore = _registry.ScoreAgainst(observationNormalizedExpanded, archetype);

    Assert.True(rawScore > normalizedScore + 0.05,
        $"raw-channel score ({rawScore:F4}) must materially exceed normalized-channel score " +
        $"({normalizedScore:F4}) when the observation populates strictly more dims than the centroid claims");

    Assert.True(rawScore >= 0.95,
        $"raw-channel score ({rawScore:F4}) must be near-perfect when raw centroid and " +
        $"observation agree exactly on every claimed dim");
}

[Fact]
public void MaskedSimilarity_FallsBackToCentroidWhenCentroidRawIsNull()
{
    // Backwards-compat: an archetype loaded from an old YAML before this commit may not have
    // CentroidRaw populated. The matcher must still produce a sensible score using Centroid.
    var dim = _encoder.Layout.Dimension;
    var raw = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    raw["network.is_datacenter"] = true;

    var centroid = _encoder.Encode(raw);
    var mask = new float[dim];
    mask[10] = 0.9f;
    var variance = new float[dim];
    Array.Fill(variance, 0.05f);

    var archetype = BuildArchetypeWithRaw("legacy", centroid, centroidRaw: null!, mask, variance);

    var observationNormalized = _encoder.Encode(raw);

    Action act = () => _registry.ScoreAgainstRaw(observationNormalized, archetype);
    act.Should().NotThrow("null CentroidRaw must fall back to Centroid without crashing");
}
```

Note: `ScoreAgainstRaw` doesn't exist yet — Task 4 adds it. Until then, this test will fail to compile, which IS the goal of TDD.

For the second test, use whichever assertion convention the file uses (the earlier conversation showed it's using xUnit `Assert.*` not FluentAssertions). Replace `.Should().NotThrow(...)` with an xUnit equivalent.

- [ ] **Step 3: Run, confirm compile failure (TDD red)**

```bash
dotnet build src/Mostlylucid.BotDetection.Orchestration.Tests/ 2>&1 | tail -10
```

Expected: compile failure on `_registry.ScoreAgainstRaw` (method not found). Good — Task 4 implements it.

- [ ] **Step 4: Commit**

```
test(identity): pin raw-channel scoring parity contract on the matcher

Two new tests, currently uncompilable:
- raw centroid + raw observation must score higher than normalized centroid +
  normalized observation when observation populates more dims than archetype claims
- null CentroidRaw falls back to Centroid (backwards-compat)

Implementation follows in the next commit (adds IdentityArchetypeRegistry.ScoreAgainstRaw).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

---

## Task 4: Implement `ScoreAgainstRaw` in the registry; switch FindNearest to use it

**File:** `src/Mostlylucid.BotDetection/Identity/IdentityArchetypeRegistry.cs`

- [ ] **Step 1: Add `ScoreAgainstRaw`**

Below the existing `ScoreAgainst` method (around line 107), add:

```csharp
/// <summary>
///     Score a raw (unnormalized) observation vector against an archetype using its raw
///     centroid (<see cref="IdentityArchetype.CentroidRaw"/>). Falls back to the normalized
///     centroid for backwards-compatibility when CentroidRaw is null.
/// </summary>
public double ScoreAgainstRaw(float[] rawVector, IdentityArchetype archetype)
{
    var centroidForScoring = archetype.CentroidRaw ?? archetype.Centroid;
    return MaskedSimilarityCore(rawVector, centroidForScoring, archetype.DimensionMask,
        archetype.VarianceVector ?? DefaultVarianceFor(archetype));
}
```

- [ ] **Step 2: Refactor `MaskedSimilarity` to share core via `MaskedSimilarityCore`**

Extract the per-dim accumulation into a parameterized helper that takes vector + centroid + mask + variance, decoupling it from the `IdentityArchetype` shape. Move the body of `MaskedSimilarity` into a private static (or instance) `MaskedSimilarityCore(float[] vector, float[] centroid, float[] mask, float[] variance)`. Keep the existing `MaskedSimilarity(float[], IdentityArchetype)` as a wrapper that delegates.

This avoids duplicating the slot-loop code between `MaskedSimilarity` and `ScoreAgainstRaw`.

- [ ] **Step 3: Switch `FindNearest` to take raw vectors**

Find `FindNearest` (around line 88). Currently it takes a normalized vector. Add an overload that takes a raw vector and uses `ScoreAgainstRaw`:

```csharp
public ArchetypeMatch? FindNearestRaw(float[] rawVector)
{
    if (_archetypes.Count == 0 || rawVector is null) return null;
    ArchetypeMatch? best = null;
    foreach (var a in _archetypes)
    {
        var s = ScoreAgainstRaw(rawVector, a);
        if (best is null || s > best.Score)
            best = new ArchetypeMatch(a, s);
    }
    return best;
}
```

Do NOT remove the existing `FindNearest`. Both coexist; callers migrate one at a time.

- [ ] **Step 4: Run the 12 unit tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~ArchetypeMatchScoringTests" 2>&1 | tail -20
```

Required: all 12 pass (10 existing + 2 new from Task 3).

- [ ] **Step 5: Commit**

```
feat(identity): ScoreAgainstRaw uses raw centroid + raw observation diffs

MaskedSimilarity refactored to MaskedSimilarityCore so the scoring loop is
shared. ScoreAgainstRaw delegates to the same core but reads CentroidRaw,
falling back to Centroid when null. FindNearestRaw added alongside FindNearest.
Callers can migrate one at a time.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

---

## Task 5: Wire the contributor and matcher caller to use raw

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Models/DetectionContext.cs`
- Modify: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/IdentityVectorContributor.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/FingerprintMatchContributor.cs`

- [ ] **Step 1: Add the signal key**

In `DetectionContext.cs`, find the existing `SignalKeys.IdentityVector` constant. Add:

```csharp
public const string IdentityVectorRaw = "identity.vector.raw";
```

- [ ] **Step 2: Write the raw vector to the blackboard**

In `IdentityVectorContributor.cs`, find where `_encoder.Encode(...)` is called and the result is written to `SignalKeys.IdentityVector`. Immediately after, also encode and write the raw vector:

```csharp
var raw = ComposeRawValues(state);
var normalized = _encoder.Encode(raw);
var rawVector = _encoder.EncodeRaw(raw);
state.WriteSignal(SignalKeys.IdentityVector, normalized);
state.WriteSignal(SignalKeys.IdentityVectorRaw, rawVector);
state.WriteSignal(SignalKeys.IdentityRawValues, raw);
```

(Use the actual method name and existing call shape; the above is the pattern, adapt to what's there.)

- [ ] **Step 3: Switch `FingerprintMatchContributor` archetype matching to raw**

In `FingerprintMatchContributor.cs`, find where `_archetypes.FindNearest(...)` is called (Pass 2 seeding, around line 409 per the prior investigation). Read the surrounding context. The call currently passes the normalized vector pulled from `SignalKeys.IdentityVector`.

Change it to pull the raw vector from `SignalKeys.IdentityVectorRaw` and call `FindNearestRaw`:

```csharp
// Was:
//   var vector = state.GetSignal<float[]>(SignalKeys.IdentityVector);
//   var match = _archetypes.FindNearest(vector);
// Now:
var rawVector = state.GetSignal<float[]>(SignalKeys.IdentityVectorRaw)
                 ?? state.GetSignal<float[]>(SignalKeys.IdentityVector); // graceful fallback
var match = _archetypes.FindNearestRaw(rawVector);
```

Only modify the archetype-matching call. Leave Pass 1 (L1 IP+UA lookup) and the Pass 2 per-fingerprint cosine matching (against persisted per-fp centroids) using the normalized vector — those are different concerns.

- [ ] **Step 4: Run the BDF replay**

```bash
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~BdfReplayTests.HumanScenario_DoesNotMisclassify" 2>&1 | tail -10
```

Required: both `fp-safari-ios-human` and `fp-chrome-ublock-xhr-mastodon-misclass` pass. If either still fails:
- Re-read what archetype was chosen (the test message names it)
- Stop and report DONE_WITH_CONCERNS with the chosen-archetype name
- Do NOT add UA-family allowlists

- [ ] **Step 5: Wider regression sweep**

```bash
dotnet build mostlylucid.stylobot.sln
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~Identity"
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~Identity"
```

Required: no new failures. If anything in the Identity sweeps regressed (e.g., cosine consumers that DO want normalized vectors are now reading raw signals by accident), fix or stop and report.

- [ ] **Step 6: Commit**

```
fix(identity): switch archetype matching to raw vectors

IdentityVectorContributor now writes both identity.vector (normalized, for
cosine consumers) and identity.vector.raw (unnormalized, for variance-aware
matching). FingerprintMatchContributor's archetype FindNearest call uses
FindNearestRaw, so per-dim diffs are computed in raw signal space.

Closes the BDF replay regression for fp-safari-ios-human and
fp-chrome-ublock-xhr-mastodon-misclass.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

---

## Task 6: Final sweep

- [ ] **Step 1: Full solution test**

```bash
dotnet test mostlylucid.stylobot.sln --filter "Category!=Integration"
```

Expected: zero failures.

- [ ] **Step 2: BDF replay confirmation**

```bash
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~BdfReplayTests"
```

Expected: all 6 scenarios pass.

---

## Out of scope (documented gaps)

- **Persisted per-fingerprint centroids stay normalized.** Pass 2 cosine matching against stored fingerprints uses the existing normalized path. If the population-density divergence shows up there too, a follow-up will add `CentroidRaw` persistence to `fingerprints` table.
- **`IdentityWeightMath.ComputeDifferentiator`** still uses normalized vector diffs. Likely also affected; out of scope for the BDF replay fix.
- **`IdentityWeightCalibrationService`** still updates `Centroid` from descendant aggregates. The CentroidRaw of refined archetypes will be stale until the calibration service learns to update both. Doesn't affect YAML-bootstrapped archetypes (rebuilt at startup), which is what the BDF replay uses.

These are documented in the commits' bodies and can be picked up in a future cycle.