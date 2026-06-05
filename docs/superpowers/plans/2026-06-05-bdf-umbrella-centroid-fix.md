# BDF Umbrella-Centroid Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `IdentityArchetypeRegistry.MaskedSimilarity` use Mahalanobis-style per-class variance so tight archetypes (chrome-desktop, firefox-*, safari-*) win the cosine race against umbrella archetypes (mastodon, googlebot, generic bots) when a Chrome+uBlock XHR strips half the Sec-Fetch headers and Upgrade-Insecure-Requests.

**Architecture:**
- Add a `VarianceVector` (float[] of identity-vector dimension) to `IdentityArchetype` and to the persisted schema. YAML may declare a per-archetype variance override; otherwise a default-from-confidence is computed at compile time.
- `MaskedSimilarity` switches from `1 / (1 + meanWeightedSqDist)` to a Mahalanobis form: per-dimension squared deviation divided by `variance[i] + ε`. Tight archetypes (low variance) penalize even small deviations; broad archetypes (high variance) tolerate them.
- The calibration service learns variance from descendant fingerprints when refining archetypes, so the matcher self-tunes as data accumulates.
- TDD: pin the fix with 6 new unit tests in `ArchetypeMatchScoringTests` that prove tight archetypes win against umbrellas for sparse Chrome XHR shapes, before the existing 2 BDF replay scenarios (`fp-safari-ios-human`, `fp-chrome-ublock-xhr-mastodon-misclass`) start passing.

**Tech Stack:** .NET 10, existing `Mostlylucid.BotDetection.Identity` namespace, SQLite for persistence, YamlDotNet for archetype YAML, xUnit + FluentAssertions.

**Design constraints (binding):**
- No UA-family allowlist in the matcher. The error message explicitly forbids it: the fix must be a generic scoring change so any new archetype added later via YAML automatically benefits.
- Detection sensitivity is not reduced in FOSS ([[feedback_foss_never_degraded]]).
- All persistent state goes through existing SQLite tables ([[feedback_no_inmemory_persistence]]).
- Fix root cause, no workarounds.
- Verify by running before committing ([[feedback_verify_before_checkin]]).

---

## File Structure

```
src/Mostlylucid.BotDetection/
  Identity/
    IdentityArchetype.cs                          # MODIFY: add VarianceVector property
    IdentityArchetypeRegistry.cs                  # MODIFY: rewrite MaskedSimilarity + compile-time default
    IdentityArchetypeYaml.cs                      # MODIFY: add optional VariancePerDimension field
    SqliteFingerprintStore.cs                     # MODIFY: schema migration + persistence
    IdentityWeightCalibrationService.cs           # MODIFY: learn variance from descendants

src/Mostlylucid.BotDetection.Orchestration.Tests/
  Unit/Identity/
    ArchetypeMatchScoringTests.cs                 # MODIFY: add 6 new tests for Mahalanobis behaviour
    ArchetypeVarianceCalibrationTests.cs          # NEW: 2 tests for the calibration learning loop
```

**Why this split:** Each file has one responsibility along the existing identity-layer architecture. The matcher (`IdentityArchetypeRegistry`), the persistence (`SqliteFingerprintStore`), and the learner (`IdentityWeightCalibrationService`) are already separated; we touch each in its own way.

---

## Task 1: Add `VarianceVector` to the in-memory model

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityArchetype.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityArchetypeYaml.cs`

- [ ] **Step 1: Add VarianceVector to the runtime record**

Open `IdentityArchetype.cs`. Find the existing record properties (Centroid, DimensionMask). Add immediately after `DimensionMask`:

```csharp
/// <summary>
///     Per-dimension variance vector used for Mahalanobis-style scoring. Tight archetypes
///     have small variance (penalize even small deviations); broad umbrella archetypes have
///     large variance (tolerate larger deviations).
///
///     Null at construction time; populated either from YAML override
///     (<see cref="IdentityArchetypeYaml.VariancePerDimension"/>) or via the default-from-confidence
///     rule applied in <c>IdentityArchetypeRegistry.Compile</c>: variance[i] = max(epsilon, (1 - confidence[i])^2 * baseScale).
/// </summary>
public float[]? VarianceVector { get; init; }
```

If `IdentityArchetype` is a positional record (`record IdentityArchetype(...)`), add to the constructor signature in canonical position right after `DimensionMask`. If it's a regular `record` with init-only properties, add as above.

- [ ] **Step 2: Add VariancePerDimension to the YAML POCO**

Open `IdentityArchetypeYaml.cs`. Add:

```csharp
/// <summary>
///     Optional per-dimension variance override (length must equal identity vector dimension).
///     When null, variance is derived from confidence values at compile time.
/// </summary>
[YamlMember(Alias = "variance_per_dimension")]
public List<float>? VariancePerDimension { get; set; }
```

Match the indent and using directives of nearby properties exactly.

- [ ] **Step 3: Verify build**

```bash
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
```

Expected: succeeds. No tests yet.

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/IdentityArchetype.cs src/Mostlylucid.BotDetection/Identity/IdentityArchetypeYaml.cs
git commit -m "$(cat <<'EOF'
feat(identity): add VarianceVector to IdentityArchetype model

Per-archetype variance vector for Mahalanobis-style scoring. Nullable for
backwards compatibility; subsequent commits compute defaults at compile time
and persist learned variance from descendant fingerprints.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: TDD: pin Mahalanobis scoring before the algorithm change

**Files:**
- Modify: `src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/Identity/ArchetypeMatchScoringTests.cs`

Read the existing file first. The plan adds new `[Fact]` methods alongside the existing tests. Do not modify existing tests.

The existing helpers in that file build archetypes via something like `BuildArchetype(id, centroid, mask)`. We need to extend or add a parallel `BuildArchetype(id, centroid, mask, variance)` helper. Find the existing builder and add an overload.

- [ ] **Step 1: Write the 6 failing tests**

Add to `ArchetypeMatchScoringTests.cs`:

```csharp
[Fact]
public void TightArchetype_WithSmallVariance_BeatsUmbrellaArchetype_WithLargeVariance_ForSparseObservation()
{
    // The umbrella problem in isolation: identical centroids, identical observation;
    // the only difference is variance. Tight (small variance) must win.
    var dim = _encoder.Layout.TotalDimensions;
    var centroid = new float[dim];
    centroid[10] = 1.0f; // both archetypes claim dim 10 = 1.0
    var mask = new float[dim];
    mask[10] = 0.9f;

    var tightVariance = new float[dim];
    Array.Fill(tightVariance, 0.001f);

    var broadVariance = new float[dim];
    Array.Fill(broadVariance, 0.1f);

    var tight = BuildArchetype("tight", centroid, mask, tightVariance);
    var broad = BuildArchetype("broad", centroid, mask, broadVariance);

    var observation = new float[dim];
    observation[10] = 0.95f; // tiny deviation

    var tightScore = ScoreAgainst(observation, tight);
    var broadScore = ScoreAgainst(observation, broad);

    tightScore.Should().BeGreaterThan(broadScore,
        "small variance must penalize less for a near-centroid observation than large variance");
}

[Fact]
public void BroadArchetype_WithLargeVariance_BeatsTightArchetype_ForDistantObservation()
{
    // Symmetry check: an observation far from both centroids should prefer the broad archetype
    // because it tolerates the deviation. This pins that variance is doing the work, not a hardcode.
    var dim = _encoder.Layout.TotalDimensions;
    var centroid = new float[dim];
    centroid[10] = 1.0f;
    var mask = new float[dim];
    mask[10] = 0.9f;

    var tightVariance = new float[dim];
    Array.Fill(tightVariance, 0.001f);
    var broadVariance = new float[dim];
    Array.Fill(broadVariance, 0.1f);

    var tight = BuildArchetype("tight", centroid, mask, tightVariance);
    var broad = BuildArchetype("broad", centroid, mask, broadVariance);

    var observation = new float[dim];
    observation[10] = 0.5f; // large deviation

    ScoreAgainst(observation, broad).Should().BeGreaterThan(
        ScoreAgainst(observation, tight),
        "large variance must tolerate large deviations better than small variance");
}

[Fact]
public void ArchetypeWithoutVariance_FallsBackToDefaultFromConfidence_AndStillScoresCorrectly()
{
    // Cold-start path: an archetype loaded with no variance vector. The default should be
    // derived from the mask (high confidence → low variance) so chrome-desktop beats mastodon
    // even without a learned variance.
    var dim = _encoder.Layout.TotalDimensions;

    var tightCentroid = new float[dim];
    tightCentroid[10] = 1.0f;
    tightCentroid[20] = 1.0f;
    var tightMask = new float[dim];
    tightMask[10] = 0.95f;
    tightMask[20] = 0.9f;

    var broadCentroid = new float[dim];
    broadCentroid[10] = 0.5f;
    var broadMask = new float[dim];
    broadMask[10] = 0.6f;

    var tight = BuildArchetype("tight", tightCentroid, tightMask, variance: null);
    var broad = BuildArchetype("broad", broadCentroid, broadMask, variance: null);

    var observation = new float[dim];
    observation[10] = 1.0f;
    observation[20] = 1.0f;

    ScoreAgainst(observation, tight).Should().BeGreaterThan(
        ScoreAgainst(observation, broad),
        "with no learned variance, the default-from-confidence rule must still favor tight high-confidence archetypes");
}

[Fact]
public void ChromeXhrLikeObservation_LandsOnTightArchetype_NotOnUmbrella()
{
    // The actual failure pattern: a Chrome+uBlock XHR has populated dims for some Sec-Fetch
    // slots but not for Upgrade-Insecure-Requests or Sec-Ch-Ua-Mobile. The tight archetype
    // (chrome-shaped) must beat the umbrella (mastodon-shaped) under Mahalanobis.
    var dim = _encoder.Layout.TotalDimensions;

    var chromeCentroid = new float[dim];
    chromeCentroid[10] = 1.0f; // sec_fetch_pattern asserted
    chromeCentroid[20] = 1.0f; // upgrade_insecure_requests asserted
    chromeCentroid[30] = 1.0f; // sec_ch_ua_mobile asserted
    var chromeMask = new float[dim];
    chromeMask[10] = 0.9f;
    chromeMask[20] = 0.9f;
    chromeMask[30] = 0.95f;
    var chromeVariance = new float[dim];
    Array.Fill(chromeVariance, 0.05f);
    chromeVariance[10] = 0.005f; // tight assertion: this dim rarely varies for real Chrome
    chromeVariance[20] = 0.005f;
    chromeVariance[30] = 0.005f;

    var umbrellaCentroid = new float[dim];
    umbrellaCentroid[10] = 0.0f; // umbrella claims this dim is 0
    var umbrellaMask = new float[dim];
    umbrellaMask[10] = 0.6f;
    var umbrellaVariance = new float[dim];
    Array.Fill(umbrellaVariance, 0.2f); // umbrella has high variance everywhere

    var chrome = BuildArchetype("chrome-tight", chromeCentroid, chromeMask, chromeVariance);
    var umbrella = BuildArchetype("umbrella", umbrellaCentroid, umbrellaMask, umbrellaVariance);

    var observation = new float[dim];
    observation[10] = 0.9f; // present, near chrome's centroid
    // dims 20 and 30 left at 0 (XHR didn't populate them; uBlock + XHR semantics)

    var chromeScore = ScoreAgainst(observation, chrome);
    var umbrellaScore = ScoreAgainst(observation, umbrella);

    chromeScore.Should().BeGreaterThan(umbrellaScore,
        "sparse observations near a tight archetype's centroid must NOT lose to broad umbrella claims");
}

[Fact]
public void Mahalanobis_HandlesZeroVariance_WithEpsilonFloor()
{
    // Numerical hygiene: variance[i] = 0 must not divide by zero. The implementation
    // floor-clamps variance with an epsilon (verify it's tight enough not to silently nullify scoring).
    var dim = _encoder.Layout.TotalDimensions;
    var centroid = new float[dim];
    centroid[10] = 1.0f;
    var mask = new float[dim];
    mask[10] = 0.9f;
    var zeroVariance = new float[dim]; // all zeros
    var archetype = BuildArchetype("zero-variance", centroid, mask, zeroVariance);

    var observation = new float[dim];
    observation[10] = 0.5f;

    Action act = () => ScoreAgainst(observation, archetype);
    act.Should().NotThrow();

    var score = ScoreAgainst(observation, archetype);
    score.Should().BeFinite("epsilon floor must keep the score finite when variance is zero");
    score.Should().BeInRange(0.0, 1.0, "score must remain in [0, 1] regardless of variance values");
}

[Fact]
public void Mahalanobis_AgainstUnpopulatedObservationDims_DoesNotPenalize()
{
    // Behavioural contract preservation: the current matcher skips dims the observation didn't
    // populate. Mahalanobis must keep that property so a partial observation (XHR mid-session)
    // doesn't get unfairly punished for missing dims it can't have.
    var dim = _encoder.Layout.TotalDimensions;
    var centroid = new float[dim];
    centroid[10] = 1.0f;
    centroid[50] = 1.0f; // archetype asserts dim 50 too
    var mask = new float[dim];
    mask[10] = 0.9f;
    mask[50] = 0.9f;
    var variance = new float[dim];
    Array.Fill(variance, 0.05f);
    var archetype = BuildArchetype("multi-dim", centroid, mask, variance);

    var fullyPopulated = new float[dim];
    fullyPopulated[10] = 1.0f;
    fullyPopulated[50] = 1.0f;

    var partiallyPopulated = new float[dim];
    partiallyPopulated[10] = 1.0f;
    // dim 50 left at 0 (unpopulated)

    var fullScore = ScoreAgainst(fullyPopulated, archetype);
    var partialScore = ScoreAgainst(partiallyPopulated, archetype);

    partialScore.Should().BeGreaterThan(0.0,
        "partial observation must not score zero");
    partialScore.Should().BeApproximately(fullScore, precision: 1e-6,
        "unpopulated dims must be skipped, not treated as deviations from centroid");
}
```

If the existing test class has a helper named other than `BuildArchetype` or `ScoreAgainst`, use the actual names. Read the file first.

If `BuildArchetype` doesn't yet accept a `variance` parameter, add the overload to the helper inline within the test file (private static method at bottom of class). Do NOT modify the existing single-arg builder; add an overload alongside it.

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~ArchetypeMatchScoringTests" 2>&1 | tail -30
```

Expected: the 6 new tests fail because `MaskedSimilarity` still uses the old formula and `VarianceVector` is ignored. The existing tests must still pass.

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/Identity/ArchetypeMatchScoringTests.cs
git commit -m "$(cat <<'EOF'
test(identity): pin Mahalanobis-style scoring contract on the archetype matcher

6 new failing tests cover:
- Tight archetypes (small variance) win against broad umbrellas for near-centroid observations
- Broad archetypes win for far observations (symmetry check)
- Default-from-confidence fallback when VarianceVector is null
- Sparse Chrome-XHR-shaped observations land on tight archetypes
- Zero-variance numerical hygiene (epsilon floor)
- Unpopulated observation dims remain skipped, not penalized

Implementation follows in the next commit.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Implement Mahalanobis in `MaskedSimilarity`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityArchetypeRegistry.cs`

- [ ] **Step 1: Replace `MaskedSimilarity` with the Mahalanobis variant**

Locate `MaskedSimilarity` (around line 135-166). Replace with:

```csharp
private double MaskedSimilarity(float[] vector, IdentityArchetype archetype)
{
    var centroid = archetype.Centroid;
    var mask = archetype.DimensionMask;
    var variance = archetype.VarianceVector ?? DefaultVarianceFor(archetype);
    const float presenceEpsilon = 1e-6f;
    const double varianceFloor = 1e-4; // tightest meaningful variance; prevents div-by-zero

    double weightedSqDist = 0, totalMask = 0;
    foreach (var slot in _encoder.Layout.Slots)
    {
        if (slot.Offset >= mask.Length) continue;
        var slotMask = (double)mask[slot.Offset];
        if (slotMask <= 0) continue;

        var end = Math.Min(slot.Offset + slot.Width, Math.Min(vector.Length, centroid.Length));
        var obsPopulated = false;
        for (var i = slot.Offset; i < end; i++)
        {
            if (Math.Abs(vector[i]) > presenceEpsilon) { obsPopulated = true; break; }
        }
        if (!obsPopulated) continue;

        for (var i = slot.Offset; i < end; i++)
        {
            double diff = vector[i] - centroid[i];
            double v = i < variance.Length ? Math.Max(varianceFloor, variance[i]) : varianceFloor;
            // Mahalanobis-style per-dimension penalty: deviation²/variance, weighted by mask confidence.
            weightedSqDist += slotMask * (diff * diff) / v;
        }
        totalMask += slotMask * (end - slot.Offset);
    }
    if (totalMask <= 0) return 0.0;
    var avg = weightedSqDist / totalMask;
    return 1.0 / (1.0 + avg);
}

private float[] DefaultVarianceFor(IdentityArchetype archetype)
{
    // Default-from-confidence rule: high-confidence dims get tight variance,
    // low-confidence dims get broad variance.
    //   variance[i] = (1 - confidence[i])² * baseScale + varianceFloor
    // baseScale is calibrated empirically; 0.05 keeps confident dims around 1e-4 and
    // unconfident dims around 0.04.
    const float baseScale = 0.05f;
    const float varianceFloor = 1e-4f;
    var mask = archetype.DimensionMask;
    var result = new float[mask.Length];
    for (var i = 0; i < mask.Length; i++)
    {
        var conf = Math.Clamp(mask[i], 0f, 1f);
        var diff = 1f - conf;
        result[i] = diff * diff * baseScale + varianceFloor;
    }
    return result;
}
```

The default-from-confidence rule means an archetype with high-confidence assertions (chrome-desktop mask ≈ 0.85-0.95) gets tight default variance, while an archetype with low-confidence assertions (mastodon mask ≈ 0.6-0.7) gets broad default variance. This delivers the umbrella fix even before any descendants have been learned.

- [ ] **Step 2: Run the new tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~ArchetypeMatchScoringTests" 2>&1 | tail -30
```

Expected: all 6 new tests pass; existing tests still pass.

- [ ] **Step 3: Run the BDF replay scenarios**

```bash
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~BdfReplayTests.HumanScenario_DoesNotMisclassify" 2>&1 | tail -10
```

Expected: both `fp-safari-ios-human` and `fp-chrome-ublock-xhr-mastodon-misclass` pass. If either still fails, the default-from-confidence baseline isn't tight enough; bump `baseScale` from 0.05 to 0.03 and re-run. If still failing after that, do NOT add a UA-family allowlist; report DONE_WITH_CONCERNS and stop.

- [ ] **Step 4: Full identity test sweep**

```bash
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~Identity"
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~Identity"
```

Expected: nothing regressed.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/IdentityArchetypeRegistry.cs
git commit -m "$(cat <<'EOF'
fix(identity): Mahalanobis scoring in MaskedSimilarity ends umbrella-centroid wins

Per-dimension squared deviation is now divided by variance, weighted by mask
confidence. Tight archetypes (small variance) penalize even small deviations;
broad umbrellas (large variance) tolerate them.

Default-from-confidence rule: variance[i] = (1 - confidence[i])² * 0.05 + ε.
High-confidence chrome-* / firefox-* / safari-* mask values yield tight default
variance; low-confidence mastodon / googlebot / generic-bot mask values yield
broad default variance. The fix lands before any descendant calibration runs.

Subsequent commits persist learned variance from descendants so the matcher
self-tunes as more fingerprints accumulate.

Closes the BDF replay regression for fp-safari-ios-human and
fp-chrome-ublock-xhr-mastodon-misclass.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Schema migration and persistence

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/SqliteFingerprintStore.cs`

- [ ] **Step 1: Add the column to the schema**

Read `SqliteFingerprintStore.cs` and find the `identity_archetypes` CREATE TABLE statement (around line 1548). Add a `variance_vector BLOB` column. Add it at the end so existing column ordering is preserved.

Add an ALTER TABLE in the migration block (find the existing `ApplyMigrations` / `ApplySchemaUpgrades` pattern; if the file uses `PRAGMA user_version` or a numbered migration list, add a new migration that appends `variance_vector` if missing). Quote the actual existing migration pattern verbatim before editing so you match conventions.

- [ ] **Step 2: Update INSERT / UPDATE / SELECT statements**

Find every SQL statement that references `identity_archetypes` columns (CREATE, INSERT, UPDATE, SELECT). For each:
- Add `variance_vector` to column lists in the same position
- Add a parameter (`@variance_vector`) in parameter binding sites
- Bind `IdentityArchetype.VarianceVector` (nullable; bind `DBNull.Value` if null) when writing
- Read it back (nullable BLOB) when loading

For SELECT-and-materialize sites, when reading, branch on `DBNull` and assign `null` to the property in that case; otherwise materialize the BLOB back into `float[]`. Use the existing float-array serialization helper in the file (search for `WriteFloatArray` / `ReadFloatArray` / `BlobToFloatArray` to find the convention).

- [ ] **Step 3: Add a fingerprint-store round-trip test**

In `src/Mostlylucid.BotDetection.Test/Identity/` (or wherever SqliteFingerprintStore tests already live; grep for `class.*SqliteFingerprintStoreTests`), add:

```csharp
[Fact]
public async Task UpsertArchetype_RoundTrip_PreservesVarianceVector()
{
    var store = NewStore(); // use the existing test factory
    var dim = 129;
    var variance = new float[dim];
    for (var i = 0; i < dim; i++) variance[i] = (float)(0.01 + 0.001 * i);

    var archetype = new IdentityArchetype
    {
        ArchetypeId = "test-variance",
        Centroid = new float[dim],
        DimensionMask = new float[dim],
        VarianceVector = variance,
        ArchetypeKind = "test"
    };

    await store.UpsertArchetypeAsync(archetype);

    var loaded = await store.GetArchetypeAsync("test-variance");

    loaded.Should().NotBeNull();
    loaded!.VarianceVector.Should().NotBeNull();
    loaded.VarianceVector!.Should().HaveCount(dim);
    loaded.VarianceVector!.Should().Equal(variance);
}

[Fact]
public async Task LoadingExistingArchetype_WithoutVarianceColumn_ReturnsNullVariance()
{
    // Backwards compatibility: a row inserted before the migration must materialize with
    // VarianceVector == null so the registry uses DefaultVarianceFor.
    var store = NewStore();
    var archetype = new IdentityArchetype
    {
        ArchetypeId = "legacy",
        Centroid = new float[129],
        DimensionMask = new float[129],
        VarianceVector = null,
        ArchetypeKind = "test"
    };

    await store.UpsertArchetypeAsync(archetype);

    var loaded = await store.GetArchetypeAsync("legacy");
    loaded!.VarianceVector.Should().BeNull();
}
```

Verify the method names (`UpsertArchetypeAsync`, `GetArchetypeAsync`) by reading the store's public surface; adjust to the actual names.

- [ ] **Step 4: Run the tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~SqliteFingerprintStoreTests"
```

Expected: both new tests pass; nothing regressed.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/SqliteFingerprintStore.cs src/Mostlylucid.BotDetection.Test/Identity/SqliteFingerprintStoreTests.cs
git commit -m "$(cat <<'EOF'
feat(identity): persist VarianceVector on identity_archetypes

Schema migration adds variance_vector BLOB column (NULL for rows written before
this commit). Insert/update/select handle the new column transparently.

Round-trip + legacy-row tests cover both directions.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Learn variance from descendant fingerprints during calibration

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityWeightCalibrationService.cs`
- Create: `src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/Identity/ArchetypeVarianceCalibrationTests.cs`

- [ ] **Step 1: Add a calibration unit test**

Create `ArchetypeVarianceCalibrationTests.cs`:

```csharp
using FluentAssertions;
using Mostlylucid.BotDetection.Identity;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

public class ArchetypeVarianceCalibrationTests
{
    [Fact]
    public void RefineArchetype_WithUniformDescendants_TightensVariance()
    {
        // Descendants are all near-identical to the centroid; learned variance must shrink
        // to near zero (clamped at the variance floor).
        var dim = 8;
        var centroid = new float[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
        var descendants = new[]
        {
            new float[] { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f },
            new float[] { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f },
            new float[] { 1.001f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f }
        };

        var variance = ArchetypeVarianceCalibrator.LearnVariance(centroid, descendants);

        variance.Should().HaveCount(dim);
        variance.All(v => v < 0.01f).Should().BeTrue(
            "uniform descendants must yield tight variance across all dims");
    }

    [Fact]
    public void RefineArchetype_WithSpreadDescendants_BroadensVariance()
    {
        var dim = 8;
        var centroid = new float[] { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f };
        var descendants = new[]
        {
            new float[] { 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f },
            new float[] { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f },
            new float[] { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f }
        };

        var variance = ArchetypeVarianceCalibrator.LearnVariance(centroid, descendants);

        variance.All(v => v > 0.1f).Should().BeTrue(
            "spread descendants must yield broad variance");
    }
}
```

- [ ] **Step 2: Add the calibrator helper**

Add a static helper class `ArchetypeVarianceCalibrator` next to the existing calibration service (`src/Mostlylucid.BotDetection/Identity/IdentityWeightCalibrationService.cs`, or as a new file `src/Mostlylucid.BotDetection/Identity/ArchetypeVarianceCalibrator.cs` if that fits the existing organization better — check what's there).

```csharp
namespace Mostlylucid.BotDetection.Identity;

internal static class ArchetypeVarianceCalibrator
{
    private const float VarianceFloor = 1e-4f;

    /// <summary>
    ///     Compute per-dimension variance of descendant vectors around the centroid.
    ///     Uses the standard sample variance formula with a hard floor to prevent
    ///     numerical degeneracy.
    /// </summary>
    public static float[] LearnVariance(float[] centroid, IReadOnlyList<float[]> descendants)
    {
        var dim = centroid.Length;
        var result = new float[dim];
        if (descendants.Count == 0)
        {
            for (var i = 0; i < dim; i++) result[i] = VarianceFloor;
            return result;
        }

        for (var i = 0; i < dim; i++)
        {
            double sumSq = 0;
            for (var d = 0; d < descendants.Count; d++)
            {
                if (i >= descendants[d].Length) continue;
                double diff = descendants[d][i] - centroid[i];
                sumSq += diff * diff;
            }
            var variance = (float)(sumSq / descendants.Count);
            result[i] = Math.Max(VarianceFloor, variance);
        }
        return result;
    }
}
```

- [ ] **Step 3: Wire the calibrator into `IdentityWeightCalibrationService`**

Find the existing refinement entry point in `IdentityWeightCalibrationService.cs` (where centroid is recomputed from descendants, around line 124-136 per the investigation report). Immediately after the centroid is recomputed, compute variance and assign to the archetype before persisting:

```csharp
// existing centroid recompute ...
var newCentroid = /* existing computation */;

// NEW:
var newVariance = ArchetypeVarianceCalibrator.LearnVariance(newCentroid, descendantVectors);

archetype = archetype with
{
    Centroid = newCentroid,
    VarianceVector = newVariance,
    // existing field updates ...
};

await _store.UpsertArchetypeAsync(archetype);
```

If the archetype is a class rather than a record, use direct property assignment. If `descendantVectors` is held in a different shape (List<IFingerprint>, dictionary, etc.), map to `float[][]` at the call site; do not change the calibrator's signature.

- [ ] **Step 4: Run the calibration tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~ArchetypeVarianceCalibrationTests"
```

Expected: both new tests pass.

- [ ] **Step 5: Full identity sweep**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~Identity"
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~Identity|FullyQualifiedName~BdfReplay"
```

Expected: all green. BDF replay scenarios pass.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/ src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/Identity/ArchetypeVarianceCalibrationTests.cs
git commit -m "$(cat <<'EOF'
feat(identity): learn per-archetype variance from descendant fingerprints

ArchetypeVarianceCalibrator computes per-dim sample variance over the
descendants when the calibration service refines a centroid. The learned
variance is persisted on UpsertArchetypeAsync and consumed on the next match.

Tight descendant distributions (real chrome users) yield tight variance that
makes the archetype hard to dislodge by sparse XHR observations. Broad
descendant distributions (the mastodon umbrella) yield broad variance that
keeps the umbrella usable for genuine fediverse traffic without stealing
chrome detections.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Final regression sweep

- [ ] **Step 1: Whole-solution build and test**

```bash
dotnet build mostlylucid.stylobot.sln
dotnet test mostlylucid.stylobot.sln --filter "Category!=Integration"
```

Expected: 0 failures. Skipped tests are the Playwright integration suite that runs only with the Demo fixture, by design.

- [ ] **Step 2: Confirm BDF replay scenarios are green**

```bash
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~BdfReplayTests"
```

Expected: all 6 BdfReplayTests scenarios pass (including `fp-safari-ios-human` and `fp-chrome-ublock-xhr-mastodon-misclass`).

- [ ] **Step 3: Open the dashboard locally and eyeball verified-bot rendering**

Per [[feedback_verify_before_checkin]]:

```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo &
DEMO=$!
sleep 6
curl -sS -A "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36" -o /dev/null http://localhost:5080/SignatureDemo
curl -sS -A "Mozilla/5.0 (compatible; Mastodonbot/4.2.1; +https://mastodon.social/about)" -o /dev/null http://localhost:5080/SignatureDemo
kill $DEMO 2>/dev/null
```

Then open the dashboard, view the latest two detections, and confirm:
- Chrome detection is named under a Chrome family archetype (chrome-desktop, chrome-xhr, or similar)
- Mastodon detection is named under a Mastodon family archetype
- Neither is misclassified as the other

If misclassification persists in the live dashboard, do NOT add a UA-family allowlist. Instead drop to DONE_WITH_CONCERNS and stop; the underlying variance floor or default scale may need tuning that's safer to do with measurement.

---

## Self-Review

**Spec coverage**
- Failing tests `fp-safari-ios-human` and `fp-chrome-ublock-xhr-mastodon-misclass` should pass after Task 3. Confirmed in Task 6 step 2.
- "No UA-family allowlist" constraint preserved: the fix is generic (variance + mask confidence), no per-family code.
- Self-tuning via descendant calibration covered in Task 5.

**Placeholder scan**
- No TBDs. Every code block is concrete.
- Task 4 step 2 deliberately calls for grep-then-edit because the SQL pattern in `SqliteFingerprintStore.cs` varies in detail across the file's CRUD methods.
- Task 5 step 3 leaves the exact in-method shape to inspection because the calibration service has internal data structures we shouldn't presume.

**Type consistency**
- `IdentityArchetype.VarianceVector` (Task 1) is the same property referenced by tests (Tasks 2, 4) and writes (Tasks 3, 5).
- `DefaultVarianceFor` in `IdentityArchetypeRegistry` is what the cold-start tests rely on.
- `ArchetypeVarianceCalibrator.LearnVariance` signature matches the test calls.

**Out of scope (deliberate)**
- Full covariance matrix (Mahalanobis with cross-dimension correlation). Diagonal Mahalanobis is the right tradeoff for now; full Σ is 16641 floats per archetype and a separate planning cycle.
- Drift-analysis Mahalanobis upgrade in `FingerprintMatchContributor.EmitConfirmedSignals` (still uses unweighted cosine for drift comparison). Future work; orthogonal to the failing tests here.
- Per-archetype YAML overrides beyond `variance_per_dimension`. The default-from-confidence rule covers all current scenarios.

---

Plan complete and saved to `docs/superpowers/plans/2026-06-05-bdf-umbrella-centroid-fix.md`.