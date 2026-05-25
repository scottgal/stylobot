# Naming via Centroid + Variance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace every hardcoded display name ("Bot", "Human", "Unknown", "Signature") with a single naming pipeline. Every visitor (bot or human) gets `"<archetype name>{ (variance)?}"`, where the archetype name comes from YAML today and from LLM-named Leiden centroids tomorrow, and the variance is decorated from already-emitted signals.

**Architecture:** The synthesizer becomes pure. It reads three foundation signals (`identity.archetype_name`, `identity.archetype_match_score`, plus existing variance signals like `session.velocity_magnitude`, `geo.country_code`) and composes a name. `FingerprintMatchContributor` is the only writer of the archetype-name signal, looking up the display name on `IdentityArchetypeRegistry` after it does the nearest-archetype match. The three IsBot gates in `DetectionBroadcastMiddleware` go away - the synthesizer runs for everyone, the view layer never invents fallback strings. Leiden discovery feeds new candidate archetypes into the same registry via a background bridge (Phase 5, separable).

**Tech Stack:** C# / .NET 10, xUnit + Moq for tests, SQLite, YAML for hand-authored archetypes.

**Critical guardrail:** Two fingerprints sharing the same archetype remain distinct fingerprints. The archetype is a centroid for `FindNearest` (display + prior), not an identity merger. Phase 5's Leiden→archetype bridge MUST NOT collapse fingerprints; it only adds new candidate centroids.

**Phasing:** Phases 1–4 are independently shippable: humans get named, view layer stops inventing strings, all hardcoded fallbacks die. Phase 5 is the self-improving loop that turns Leiden discoveries into named archetypes; without it, archetype names come only from YAML.

---

## File Structure

### New files
- `src/Mostlylucid.BotDetection.Test/Services/VarianceNameTests.cs` - focused tests for variance composition

### Modified files
- `src/Mostlylucid.BotDetection/Services/DeterministicBotNameSynthesizer.cs` - rewrite the name-composition logic
- `src/Mostlylucid.BotDetection/Models/DetectionContext.cs` - add `IdentityArchetypeName`, `IdentityArchetypeDescription` signal key constants
- `src/Mostlylucid.BotDetection/Identity/IdentityArchetypeRegistry.cs` - add `TryGetById(string)` lookup
- `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintMatchContributor.cs` - write archetype-name signal whenever a match is made
- `src/Mostlylucid.BotDetection.UI/Middleware/DetectionBroadcastMiddleware.cs` - drop IsBot gates at lines 172, 360, 378
- `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbBadge/Default.cshtml` - drop hardcoded "Human"
- `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbSummary/Default.cshtml` - drop hardcoded "Human"
- `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbSummary/Card.cshtml` - drop hardcoded "Human"
- `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbSessionsList/Default.cshtml` - keep signature-hash fallback (synthesizer should always fire, but cold render still needs it)
- `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbThreatsList/Default.cshtml` - drop hardcoded "Unknown"
- `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/BotDetectionDetails/Default.cshtml` - show name unconditionally
- `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SignatureDetail.cshtml` - drop hardcoded "Signature"
- `src/Mostlylucid.BotDetection.Test/Services/DeterministicBotNameTests.cs` - add human-naming tests; preserve bot-naming tests
- `src/Mostlylucid.BotDetection/Models/DetectionContext.cs` - (Phase 5) add `IdentityArchetypeOrigin = "leiden-discovered"` constant if not present

### Phase 5 new files
- `src/Mostlylucid.BotDetection/Identity/ArchetypeDiscoveryService.cs` - promotes mature Leiden clusters into named archetype centroids
- `src/Mostlylucid.BotDetection.Test/Identity/ArchetypeDiscoveryServiceTests.cs` - including the "fingerprints stay distinct within an archetype" assertion

---

## Phase 1 - Surface the archetype display name as a foundation signal

### Task 1.1: Add archetype-name signal keys

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Models/DetectionContext.cs` (the `SignalKeys` `Identity` block around line 459)

- [ ] **Step 1.1.1: Add the two new signal-key constants**

In `DetectionContext.cs`, in the `Identity` block of `SignalKeys`, add after `IdentityClientTypeOrigin`:

```csharp
    /// <summary>string: human-readable display name of the matched archetype (e.g. "Chrome on Windows", "python-requests"). Written by FingerprintMatchContributor whenever a match resolves to an archetype.</summary>
    public const string IdentityArchetypeName = "identity.archetype_name";

    /// <summary>string?: optional descriptive text for the matched archetype. Written by FingerprintMatchContributor when present.</summary>
    public const string IdentityArchetypeDescription = "identity.archetype_description";
```

- [ ] **Step 1.1.2: Commit**

```bash
git add src/Mostlylucid.BotDetection/Models/DetectionContext.cs
git commit -m "$(cat <<'EOF'
feat(identity): add archetype display-name signal keys

Adds identity.archetype_name and identity.archetype_description so
downstream consumers (synthesizer, dashboard) can read the matched
archetype's display name without injecting IdentityArchetypeRegistry.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 1.2: Add `TryGetById` to `IdentityArchetypeRegistry`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityArchetypeRegistry.cs`
- Test: `src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/Identity/IdentityArchetypeRegistryTests.cs` (create if missing)

- [ ] **Step 1.2.1: Write the failing test**

Create or extend `IdentityArchetypeRegistryTests.cs`:

```csharp
[Fact]
public void TryGetById_ReturnsArchetype_WhenIdMatches()
{
    var registry = BuildRegistry(); // loads embedded YAML
    var found = registry.TryGetById("python-requests");

    Assert.NotNull(found);
    Assert.Equal("python-requests", found!.ArchetypeId);
    Assert.False(string.IsNullOrEmpty(found.Name));
}

[Fact]
public void TryGetById_ReturnsNull_WhenIdUnknown()
{
    var registry = BuildRegistry();
    Assert.Null(registry.TryGetById("does-not-exist"));
}

private static IdentityArchetypeRegistry BuildRegistry() =>
    new(NullLogger<IdentityArchetypeRegistry>.Instance, new IdentityVectorEncoder(...));
```

- [ ] **Step 1.2.2: Run the test, confirm it fails to compile**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~IdentityArchetypeRegistryTests" --no-build`
Expected: compile error - `TryGetById` does not exist on `IdentityArchetypeRegistry`.

- [ ] **Step 1.2.3: Add `TryGetById` to the registry**

In `IdentityArchetypeRegistry.cs`, after the existing `All` property:

```csharp
    /// <summary>
    ///     Lookup by archetype id (case-insensitive). Returns null if not present.
    /// </summary>
    public IdentityArchetype? TryGetById(string archetypeId)
    {
        if (string.IsNullOrEmpty(archetypeId)) return null;
        foreach (var a in _archetypes)
            if (string.Equals(a.ArchetypeId, archetypeId, StringComparison.OrdinalIgnoreCase))
                return a;
        return null;
    }
```

- [ ] **Step 1.2.4: Run tests, confirm pass**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~IdentityArchetypeRegistryTests"`
Expected: PASS.

- [ ] **Step 1.2.5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/IdentityArchetypeRegistry.cs \
        src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/Identity/IdentityArchetypeRegistryTests.cs
git commit -m "$(cat <<'EOF'
feat(identity): IdentityArchetypeRegistry.TryGetById lookup

Enables consumers to resolve an archetype's display name from its id
without iterating the All collection. Used by FingerprintMatchContributor
to write the archetype-name signal.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 1.3: Write archetype-name signals from `FingerprintMatchContributor`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintMatchContributor.cs`
- Test: `src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/Identity/FingerprintMatchContributorTests.cs` (add to existing if present)

- [ ] **Step 1.3.1: Find both write sites**

In `FingerprintMatchContributor.cs`, the two existing write blocks are at line ~278 (new fingerprint) and line ~299 (matched fingerprint). Both already write `IdentityClientType`; both should additionally write `IdentityArchetypeName` and (if present) `IdentityArchetypeDescription`.

- [ ] **Step 1.3.2: Write failing tests for both code paths**

Add to `FingerprintMatchContributorTests.cs`:

```csharp
[Fact]
public async Task NewFingerprint_WritesArchetypeName_WhenArchetypeMatched()
{
    var (state, contributor) = BuildHarness(seedArchetype: ("python-requests", "Python requests library"));
    // Build a vector that matches the python-requests archetype's centroid
    state.WriteSignal(SignalKeys.IdentityVector, BuildVectorMatching("python-requests"));

    await contributor.ContributeAsync(state, CancellationToken.None);

    Assert.Equal("python-requests", state.GetSignal<string>(SignalKeys.IdentityArchetypeName));
    Assert.Equal("Python requests library", state.GetSignal<string>(SignalKeys.IdentityArchetypeDescription));
}

[Fact]
public async Task MatchedFingerprint_WritesArchetypeName_FromPriorClientType()
{
    var (state, contributor) = BuildHarness(seedArchetype: ("chrome-desktop", "Chrome on desktop"));
    // Pre-seed an existing fingerprint with InferredClientType = "chrome-desktop"
    SeedFingerprint(state, "chrome-desktop");
    state.WriteSignal(SignalKeys.IdentityVector, BuildVectorMatching("chrome-desktop"));

    await contributor.ContributeAsync(state, CancellationToken.None);

    Assert.Equal("chrome-desktop", state.GetSignal<string>(SignalKeys.IdentityArchetypeName));
}

[Fact]
public async Task Match_DoesNotWriteArchetypeName_WhenNoArchetypeAvailable()
{
    var (state, contributor) = BuildHarness(seedArchetype: null);
    state.WriteSignal(SignalKeys.IdentityVector, BuildEmptyVector());

    await contributor.ContributeAsync(state, CancellationToken.None);

    Assert.Null(state.GetSignal<string>(SignalKeys.IdentityArchetypeName));
}
```

- [ ] **Step 1.3.3: Run tests, confirm fail**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~FingerprintMatchContributorTests"`
Expected: the new tests FAIL (signal absent).

- [ ] **Step 1.3.4: Write the archetype-name signal at the new-fingerprint site**

In `FingerprintMatchContributor.cs`, in the new-fingerprint branch after `state.WriteSignal(SignalKeys.IdentityClientType, newFp.InferredClientType);`:

```csharp
        if (nearestArchetype is not null)
        {
            var arch = nearestArchetype.Archetype;
            state.WriteSignal(SignalKeys.IdentityArchetypeName, arch.Name);
            if (!string.IsNullOrEmpty(arch.Description))
                state.WriteSignal(SignalKeys.IdentityArchetypeDescription, arch.Description);
        }
```

- [ ] **Step 1.3.5: Write the archetype-name signal at the matched-fingerprint site**

In the matched-fingerprint branch, after `state.WriteSignal(SignalKeys.IdentityClientType, matched.InferredClientType);`, resolve the archetype by id:

```csharp
        var matchedArchetype = !string.IsNullOrEmpty(matched.InferredClientType)
            ? _archetypes.TryGetById(matched.InferredClientType)
            : null;
        if (matchedArchetype is not null)
        {
            state.WriteSignal(SignalKeys.IdentityArchetypeName, matchedArchetype.Name);
            if (!string.IsNullOrEmpty(matchedArchetype.Description))
                state.WriteSignal(SignalKeys.IdentityArchetypeDescription, matchedArchetype.Description);
        }
```

- [ ] **Step 1.3.6: Run tests, confirm pass**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~FingerprintMatchContributorTests"`
Expected: PASS.

- [ ] **Step 1.3.7: Run the BDF integration rig, confirm no regression**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "Category=Integration&FullyQualifiedName~BdfReplayTests"`
Expected: PASS. The signal probes already cover `IdentityFingerprintId` and `UserAgentFamily`; the new signals are additive and should not break anything.

- [ ] **Step 1.3.8: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintMatchContributor.cs \
        src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/Identity/FingerprintMatchContributorTests.cs
git commit -m "$(cat <<'EOF'
feat(identity): FingerprintMatchContributor emits archetype name signal

Both the new-fingerprint and matched-fingerprint code paths now write
identity.archetype_name (and optionally identity.archetype_description)
when the match resolves to a known archetype. This lets the name
synthesizer compose a display name without injecting the registry.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase 2 - REVISED: drift-analysis variance composer

> **Revision (2026-05-16, after user clarification):** Variance is not a set of hand-coded rules (`if velocity > 0.5 then Rotating`). It is **per-slot scaled distance** between the fingerprint's identity vector and the matched archetype's centroid, weighted by the calibrated per-dimension weights from `IdentityWeightCalibrationService` (Fisher-derived). The slot with the largest scaled distance IS the distinguishing feature. The slot's name maps to a human-readable label.
>
> **Why this is better:** It generalises. Any future named slot (a new TLS dim, a new client-hint header, a new tool tell) automatically participates in naming without code changes. The synthesizer's variance branch becomes a slot-name → label dictionary, not a chain of `if velocity > X` rules. It is also exactly the same shape of math the matcher uses, so naming and matching share an intuition.
>
> The original Phase 2 tasks below are SUPERSEDED. Use the revised tasks here.

### Task 2.0: Add per-slot drift signal keys

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Models/DetectionContext.cs`

- [ ] **Step 2.0.1: Add three new signal-key constants**

In the `Identity` block of `SignalKeys`, after `IdentityArchetypeDominantCountry`:

```csharp
    /// <summary>string?: name of the layout slot with the largest scaled distance between the observed identity vector and the matched archetype's centroid (e.g. "network.country", "hdr.sec_ch_ua_brands_ordered"). Written by FingerprintMatchContributor after match. Null when no archetype matched.</summary>
    public const string IdentityDriftTopSlot = "identity.drift_top_slot";

    /// <summary>double: Fisher-weighted L2 distance for the top-drift slot (lower = closer to centroid). Range loosely 0..N depending on slot width.</summary>
    public const string IdentityDriftTopScore = "identity.drift_top_score";

    /// <summary>string?: coarse category prefix of the top-drift slot ("network", "locale", "hdr", "tool", "tls", "behaviour"). Lets the synthesizer map drift to a label class without parsing the full slot name.</summary>
    public const string IdentityDriftTopCategory = "identity.drift_top_category";
```

- [ ] **Step 2.0.2: Commit**

```bash
git add src/Mostlylucid.BotDetection/Models/DetectionContext.cs
git commit -m "feat(identity): add drift-top-slot signal keys"
```

---

### Task 2.1: Add per-slot scaled-distance helper to `IdentityWeightMath`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityWeightMath.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Identity/IdentityWeightMathDriftTests.cs` (create)

- [ ] **Step 2.1.1: Write the failing test**

```csharp
public class IdentityWeightMathDriftTests
{
    [Fact]
    public void TopDriftSlot_ReturnsSlotWithLargestScaledDistance()
    {
        var layout = IdentityVectorLayout.DefaultV1();
        var dim = layout.Dimension;
        var centroid = new float[dim];
        var observed = new float[dim];
        var weights = Enumerable.Repeat(1.0f, dim).ToArray();

        // Inject a unit deviation in one slot - say "network.country" (offset/width per layout).
        var countrySlot = layout.FindSlot("network.country")!;
        for (var i = countrySlot.Offset; i < countrySlot.Offset + countrySlot.Width; i++)
            observed[i] = 1.0f;

        var result = IdentityWeightMath.TopDriftSlot(observed, centroid, weights, layout);

        Assert.NotNull(result);
        Assert.Equal("network.country", result!.Value.SlotName);
        Assert.True(result.Value.Score > 0);
    }

    [Fact]
    public void TopDriftSlot_ReturnsNull_WhenVectorsIdentical()
    {
        var layout = IdentityVectorLayout.DefaultV1();
        var v = new float[layout.Dimension];
        var w = Enumerable.Repeat(1.0f, layout.Dimension).ToArray();

        Assert.Null(IdentityWeightMath.TopDriftSlot(v, v, w, layout));
    }

    [Fact]
    public void TopDriftSlot_RespectsWeights()
    {
        // Two slots deviate equally; the one with higher weight should win.
        var layout = IdentityVectorLayout.DefaultV1();
        var dim = layout.Dimension;
        var centroid = new float[dim];
        var observed = new float[dim];
        var weights = Enumerable.Repeat(1.0f, dim).ToArray();

        var slotA = layout.FindSlot("network.country")!;
        var slotB = layout.FindSlot("network.asn")!;

        for (var i = slotA.Offset; i < slotA.Offset + slotA.Width; i++) observed[i] = 1.0f;
        for (var i = slotB.Offset; i < slotB.Offset + slotB.Width; i++) observed[i] = 1.0f;

        // Boost slot B's weight so it should win the tie.
        for (var i = slotB.Offset; i < slotB.Offset + slotB.Width; i++) weights[i] = 10.0f;

        var result = IdentityWeightMath.TopDriftSlot(observed, centroid, weights, layout);

        Assert.NotNull(result);
        Assert.Equal("network.asn", result!.Value.SlotName);
    }
}
```

- [ ] **Step 2.1.2: Add `TopDriftSlot` to `IdentityWeightMath`**

```csharp
    public readonly record struct DriftResult(string SlotName, double Score, string Category);

    /// <summary>
    ///     Returns the slot with the largest weighted L2 distance between observed and centroid,
    ///     where each dimension's contribution is squared-difference times that dimension's weight.
    ///     This is the per-slot analogue of the Mahalanobis distance the matcher uses globally;
    ///     here we surface WHICH slot is drifting, not just the global drift magnitude.
    ///     Returns null when vectors are identical or inputs are length-mismatched.
    /// </summary>
    public static DriftResult? TopDriftSlot(
        ReadOnlySpan<float> observed,
        ReadOnlySpan<float> centroid,
        ReadOnlySpan<float> weights,
        IdentityVectorLayout layout)
    {
        if (observed.Length != centroid.Length || observed.Length != weights.Length) return null;
        if (observed.Length != layout.Dimension) return null;

        string? bestSlot = null;
        var bestScore = 0.0;
        foreach (var slot in layout.Slots)
        {
            var score = 0.0;
            for (var i = slot.Offset; i < slot.Offset + slot.Width; i++)
            {
                var diff = observed[i] - centroid[i];
                score += diff * diff * weights[i];
            }
            if (score > bestScore)
            {
                bestScore = score;
                bestSlot = slot.Name;
            }
        }
        if (bestSlot is null) return null;

        var category = bestSlot.IndexOf('.') is var dot and > 0 ? bestSlot[..dot] : bestSlot;
        return new DriftResult(bestSlot, bestScore, category);
    }
```

- [ ] **Step 2.1.3: Run, commit**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~IdentityWeightMathDriftTests"
```

Commit:

```bash
git add src/Mostlylucid.BotDetection/Identity/IdentityWeightMath.cs \
        src/Mostlylucid.BotDetection.Test/Identity/IdentityWeightMathDriftTests.cs
git commit -m "feat(identity): per-slot drift analysis via Fisher-weighted L2"
```

---

### Task 2.2: `FingerprintMatchContributor` writes drift signals after match

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintMatchContributor.cs`
- Test: `src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/Identity/FingerprintMatchContributorDriftTests.cs` (create)

The matched-fingerprint branch already has the observation vector and the matched archetype's centroid in scope. Add a drift computation right after the archetype-name signal write from Task 1.3, gated on weights availability.

- [ ] **Step 2.2.1: Write failing tests**

```csharp
public class FingerprintMatchContributorDriftTests
{
    [Fact]
    public async Task Match_WithCountryDrift_WritesNetworkCountrySlot()
    {
        // Archetype centroid matches "chrome-desktop" baseline (DE country slot).
        // Observation has US in the country slot.
        var (state, contributor) = BuildHarness();
        SeedArchetypeWithDominantCountry("chrome-desktop", "DE");
        var observation = BuildObservedVectorWithCountry("US");
        state.WriteSignal(SignalKeys.IdentityVector, observation);

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.Equal("network.country", state.GetSignal<string>(SignalKeys.IdentityDriftTopSlot));
        Assert.Equal("network", state.GetSignal<string>(SignalKeys.IdentityDriftTopCategory));
        Assert.True(state.GetSignal<double>(SignalKeys.IdentityDriftTopScore) > 0);
    }

    [Fact]
    public async Task Match_WithNoDrift_DoesNotWriteDriftSignals()
    {
        var (state, contributor) = BuildHarness();
        SeedArchetype("chrome-desktop", centroid: BuildBaseVector());
        state.WriteSignal(SignalKeys.IdentityVector, BuildBaseVector()); // identical

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.Null(state.GetSignal<string>(SignalKeys.IdentityDriftTopSlot));
    }
}
```

- [ ] **Step 2.2.2: Add the drift computation**

In `FingerprintMatchContributor`, in both the new-fingerprint and matched-fingerprint branches, after the archetype-name signal write:

```csharp
        if (matchedArchetype is not null)
        {
            var observed = GetObservedVector(state); // existing helper
            var weights = _calibration.CurrentGlobalWeights ?? Enumerable.Repeat(1.0f, observed.Length).ToArray();
            var drift = IdentityWeightMath.TopDriftSlot(observed, matchedArchetype.Centroid, weights, _layout);
            if (drift is not null && drift.Value.Score > _options.DriftEpsilon)
            {
                state.WriteSignal(SignalKeys.IdentityDriftTopSlot, drift.Value.SlotName);
                state.WriteSignal(SignalKeys.IdentityDriftTopCategory, drift.Value.Category);
                state.WriteSignal(SignalKeys.IdentityDriftTopScore, drift.Value.Score);
            }
        }
```

`DriftEpsilon` is a new option (default 0.05) that prevents tiny float noise from naming everyone "drifting".

- [ ] **Step 2.2.3: Run, commit**

```bash
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName~FingerprintMatchContributorDriftTests"
```

Commit:

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintMatchContributor.cs \
        src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs \
        src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/Identity/FingerprintMatchContributorDriftTests.cs
git commit -m "feat(identity): emit drift-top-slot signals after match"
```

---

### Task 2.3: Synthesizer composes name from archetype + drift slot

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Services/DeterministicBotNameSynthesizer.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Services/DeterministicBotNameTests.cs`

**Slot-to-label dictionary** (the only piece of human authoring):

| Slot category | Slot name | Label template |
|---|---|---|
| network | `network.country` | `"from {geo.country_code}"` |
| network | `network.asn` | `"new ASN"` |
| network | `network.is_datacenter` | `"datacenter"` |
| network | `network.is_vpn` | `"VPN"` |
| network | `network.is_tor` | `"Tor"` |
| locale | `locale.accept_language_primary` | `"language shift"` |
| hdr | `hdr.accept` / `hdr.accept_encoding_ordered` | `"stripped headers"` |
| hdr | `hdr.sec_ch_ua_*` | `"missing client hints"` |
| hdr | `hdr.upgrade_insecure_requests` / `hdr.dnt` / `hdr.sec_gpc` | `"privacy headers"` |
| hdr | `hdr.header_order_hash` / `hdr.header_case_pattern` | `"reordered headers"` |
| tool | any `tool.*` | `"tooled"` |
| (fallback) | any other | `"drifted"` |

- [ ] **Step 2.3.1: Add the synthesizer logic**

Replace `GetVarianceTerm` from the original (superseded) Phase 2 with:

```csharp
    private static string? GetVarianceTerm(IReadOnlyDictionary<string, object?> signals)
    {
        var slot = GetString(signals, "identity.drift_top_slot");
        if (string.IsNullOrEmpty(slot)) return null;

        // Slot-specific labels first; fall through to category, then a generic fallback.
        var country = GetString(signals, "geo.country_code");
        return slot switch
        {
            "network.country" when !string.IsNullOrEmpty(country) => $"from {country}",
            "network.country" => "geo shift",
            "network.asn" => "new ASN",
            "network.is_datacenter" => "datacenter",
            "network.is_vpn" => "VPN",
            "network.is_tor" => "Tor",
            "locale.accept_language_primary" or "locale.accept_language_count" => "language shift",
            "hdr.accept" or "hdr.accept_encoding_ordered" => "stripped headers",
            "hdr.header_order_hash" or "hdr.header_case_pattern" => "reordered headers",
            "hdr.upgrade_insecure_requests" or "hdr.dnt" or "hdr.sec_gpc" => "privacy headers",
            var s when s.StartsWith("hdr.sec_ch_ua_", StringComparison.OrdinalIgnoreCase) => "missing client hints",
            var s when s.StartsWith("tool.", StringComparison.OrdinalIgnoreCase) => "tooled",
            _ => GetString(signals, "identity.drift_top_category") switch
            {
                "network" => "network drift",
                "hdr" => "header drift",
                "locale" => "locale drift",
                "tool" => "tooled",
                _ => "drifted"
            }
        };
    }
```

- [ ] **Step 2.3.2: Rewrite tests around drift signals (not hand-coded thresholds)**

Replace the variance-composition tests from the superseded Phase 2 with:

```csharp
[Fact]
public async Task ArchetypeName_PlusDriftSlot_ComposesVarianceLabel()
{
    var signals = new Dictionary<string, object?>
    {
        ["identity.archetype_name"] = "Chrome on Windows",
        ["identity.drift_top_slot"] = "network.country",
        ["identity.drift_top_category"] = "network",
        ["geo.country_code"] = "JP"
    };

    var name = await _synthesizer.SynthesizeBotNameAsync(signals);

    Assert.Contains("Chrome on Windows", name);
    Assert.Contains("from JP", name);
}

[Fact]
public async Task UnknownSlot_FallsBackToCategoryLabel()
{
    var signals = new Dictionary<string, object?>
    {
        ["identity.archetype_name"] = "Safari on iOS",
        ["identity.drift_top_slot"] = "network.some_future_dim",
        ["identity.drift_top_category"] = "network"
    };

    var name = await _synthesizer.SynthesizeBotNameAsync(signals);

    Assert.Contains("network drift", name);
}

[Fact]
public async Task ToolSlotDrift_GetsTooledLabel()
{
    var signals = new Dictionary<string, object?>
    {
        ["identity.archetype_name"] = "Chrome on Windows",
        ["identity.drift_top_slot"] = "tool.x_requested_with",
        ["identity.drift_top_category"] = "tool"
    };

    var name = await _synthesizer.SynthesizeBotNameAsync(signals);

    Assert.Contains("tooled", name);
}

[Fact]
public async Task NoDriftSignal_ProducesPlainArchetypeName()
{
    var signals = new Dictionary<string, object?>
    {
        ["identity.archetype_name"] = "Chrome on Windows"
    };

    var name = await _synthesizer.SynthesizeBotNameAsync(signals);

    Assert.StartsWith("Chrome on Windows", name);
    Assert.DoesNotContain("(", name.Replace($" (US:", "")); // no parenthetical variance
}
```

The original `HighVelocity_GetsRotatingPrefix`, `NoAssets_GetsHeadlessPrefix` and `ScanningIntent_GetsScannerNoun` tests stay green only if the synthesizer keeps a small back-compat path for bot-only signals. Decide whether to keep those signals as a second-priority drift source or delete those tests when the matcher path becomes authoritative. Default: delete them - drift is the new contract.

- [ ] **Step 2.3.3: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/DeterministicBotNameSynthesizer.cs \
        src/Mostlylucid.BotDetection.Test/Services/DeterministicBotNameTests.cs
git commit -m "$(cat <<'EOF'
feat(naming): variance composer reads drift-top-slot signals

Replaces the hand-coded `if velocity > X` variance rules with a
slot-name → label dictionary keyed on identity.drift_top_slot. The
top-drift slot itself is computed by FingerprintMatchContributor as
the named slot with the largest Fisher-weighted L2 distance between
the observed identity vector and the matched archetype's centroid.
Adding a new named slot to the vector layout automatically participates
in naming without synthesizer changes; only the label dictionary needs
extending when a new category should get a distinct label.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Original Phase 2 tasks (SUPERSEDED, kept for context)

The tasks below predate the drift-analysis clarification. They documented a hand-coded variance pipeline (`velocity_magnitude > 0.5` → `"Rotating"`, etc.). The revised tasks above replace them entirely; this section stays as audit trail.

### Task 2.1 [SUPERSEDED]: Specify the new naming contract via tests

**Files:**
- Modify: `src/Mostlylucid.BotDetection.Test/Services/DeterministicBotNameTests.cs`

The new contract:

| Priority | Branch | Output shape |
|---|---|---|
| 1 | `ua.bot_name` present and non-"unknown" | `<bot_name>` + `Unique(country, sig)` (existing behaviour, preserve) |
| 2 | `identity.archetype_name` present | `<archetype_name>` + optional variance term + `Unique(country, sig)` |
| 3 | `ua.family` present | `<family>` (no "Bot" suffix) + optional variance term + `Unique(country, sig)` |
| 4 | everything empty | `"analysing"` + `Unique(country, sig)` (unchanged) |

Variance term (single term, picked by priority): `Rotating` (velocity > 0.5) → `Travelling (<archetype dominant_country>)` (geo divergence) → `Headless` (asset_ratio < 0.01 AND page_rate > 2) → `Bursty` (burst_ratio > 0.5) → `New` (identity.is_new_fingerprint AND archetype_match_score < 0.5) → none.

- [ ] **Step 2.1.1: Add tests for the archetype-name branch (human Chrome visitor)**

In `DeterministicBotNameTests.cs`, append:

```csharp
[Fact]
public async Task ArchetypeName_UsedAsBase_WhenPresent()
{
    var signals = new Dictionary<string, object?>
    {
        ["identity.archetype_name"] = "Chrome on Windows",
        ["ua.family"] = "Chrome",
        ["intent.category"] = "browsing",
        ["geo.country_code"] = "US"
    };

    var name = await _synthesizer.SynthesizeBotNameAsync(signals);

    Assert.NotNull(name);
    Assert.StartsWith("Chrome on Windows", name);
    Assert.DoesNotContain("Automated", name);
    Assert.DoesNotContain("Bot", name);
}

[Fact]
public async Task Human_GetsFamilyName_WhenNoArchetypeAndNoBotEvidence()
{
    var signals = new Dictionary<string, object?>
    {
        ["ua.family"] = "Firefox",
        ["intent.category"] = "browsing",
        ["geo.country_code"] = "GB"
    };

    var name = await _synthesizer.SynthesizeBotNameAsync(signals);

    Assert.NotNull(name);
    Assert.StartsWith("Firefox", name);
    Assert.DoesNotContain("Automated", name);
    Assert.DoesNotContain("Bot", name);
}

[Fact]
public async Task Human_NeverNamedAutomatedBot()
{
    // Regression: previously a Chrome visitor would synthesize as "Automated Bot"
    var signals = new Dictionary<string, object?>
    {
        ["ua.family"] = "Chrome",
        ["ua.bot_name"] = "",
        ["ua.bot_type"] = "",
        ["intent.category"] = "browsing"
    };

    var name = await _synthesizer.SynthesizeBotNameAsync(signals);

    Assert.NotEqual("Automated Bot", name);
    Assert.DoesNotMatch(@"^Automated\s+Bot", name);
}
```

- [ ] **Step 2.1.2: Add tests for variance composition**

```csharp
[Fact]
public async Task ArchetypeWithRotation_GetsRotatingVariance()
{
    var signals = new Dictionary<string, object?>
    {
        ["identity.archetype_name"] = "Chrome on Windows",
        ["session.velocity_magnitude"] = 0.8
    };

    var name = await _synthesizer.SynthesizeBotNameAsync(signals);

    Assert.Contains("Chrome on Windows", name);
    Assert.Contains("Rotating", name);
}

[Fact]
public async Task ArchetypeWithGeoDivergence_GetsTravellingVariance()
{
    var signals = new Dictionary<string, object?>
    {
        ["identity.archetype_name"] = "Safari on iOS",
        ["identity.archetype_dominant_country"] = "DE",
        ["geo.country_code"] = "JP"
    };

    var name = await _synthesizer.SynthesizeBotNameAsync(signals);

    Assert.Contains("Safari on iOS", name);
    Assert.Contains("Travelling", name);
}

[Fact]
public async Task NewFingerprintWithLowMatch_GetsNewVariance()
{
    var signals = new Dictionary<string, object?>
    {
        ["identity.archetype_name"] = "Chrome on Windows",
        ["identity.is_new_fingerprint"] = true,
        ["identity.match_score"] = 0.3
    };

    var name = await _synthesizer.SynthesizeBotNameAsync(signals);

    Assert.Contains("Chrome on Windows", name);
    Assert.Contains("New", name);
}

[Fact]
public async Task NoNotableVariance_ProducesPlainArchetypeName()
{
    var signals = new Dictionary<string, object?>
    {
        ["identity.archetype_name"] = "Chrome on Windows",
        ["session.velocity_magnitude"] = 0.1
    };

    var name = await _synthesizer.SynthesizeBotNameAsync(signals);

    Assert.StartsWith("Chrome on Windows", name);
    Assert.DoesNotContain("Rotating", name);
    Assert.DoesNotContain("Travelling", name);
    Assert.DoesNotContain("Headless", name);
    Assert.DoesNotContain("Bursty", name);
}
```

- [ ] **Step 2.1.3: Run tests, confirm new ones fail**

Run: `dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~DeterministicBotNameTests"`
Expected: existing tests pass; new tests FAIL on contents (the current implementation produces "Automated Bot" for the human cases).

- [ ] **Step 2.1.4: Commit the failing tests**

```bash
git add src/Mostlylucid.BotDetection.Test/Services/DeterministicBotNameTests.cs
git commit -m "$(cat <<'EOF'
test(naming): tests for archetype + variance synthesis contract

Adds RED tests covering: archetype-name as base, human-shaped naming
without bot suffix, and variance decoration (Rotating/Travelling/New/
no-variance). The current synthesizer fails these because of the
'Automated Bot' default; the next commit rewrites it.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2.2: Rewrite `DeterministicBotNameSynthesizer.GenerateName`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Services/DeterministicBotNameSynthesizer.cs`

- [ ] **Step 2.2.1: Replace the GenerateName method**

In `DeterministicBotNameSynthesizer.cs`, replace the existing `GenerateName` with:

```csharp
    private static string GenerateName(IReadOnlyDictionary<string, object?> signals)
    {
        var signature = GetString(signals, "signature.primary");
        var country = GetString(signals, "geo.country_code");

        // Priority 1: known bot name from UA parsing (highest confidence).
        var botName = GetString(signals, "ua.bot_name");
        if (!string.IsNullOrEmpty(botName) && botName != "unknown")
            return Unique(botName, signature, country);

        // Priority 2: matched archetype display name + variance decoration.
        var archetypeName = GetString(signals, "identity.archetype_name");
        if (!string.IsNullOrEmpty(archetypeName))
        {
            var variance = GetVarianceTerm(signals);
            var composed = string.IsNullOrEmpty(variance)
                ? archetypeName
                : $"{archetypeName} ({variance})";
            return Unique(composed, signature, country);
        }

        // Priority 3: UA family fallback (humans on first request, before archetype match).
        var family = GetString(signals, "ua.family");
        if (!string.IsNullOrEmpty(family))
        {
            var variance = GetVarianceTerm(signals);
            var composed = string.IsNullOrEmpty(variance) ? family : $"{family} ({variance})";
            return Unique(composed, signature, country);
        }

        // Priority 4: cold state, no UA info at all.
        return Unique("analysing", signature, country);
    }

    /// <summary>
    ///     Returns a single variance term derived from behavioural signals, or null when
    ///     nothing notable is happening. Variance terms describe *how this fingerprint
    ///     deviates from the population*, not what type of client it is - the archetype name
    ///     handles the latter.
    /// </summary>
    private static string? GetVarianceTerm(IReadOnlyDictionary<string, object?> signals)
    {
        // Highest-signal variance first; only one term is appended.
        if (GetDouble(signals, "session.velocity_magnitude") > 0.5) return "Rotating";

        var archetypeCountry = GetString(signals, "identity.archetype_dominant_country");
        var observedCountry = GetString(signals, "geo.country_code");
        if (!string.IsNullOrEmpty(archetypeCountry) && !string.IsNullOrEmpty(observedCountry)
            && !string.Equals(archetypeCountry, observedCountry, StringComparison.OrdinalIgnoreCase))
            return "Travelling";

        if (GetDouble(signals, "waveform.asset_ratio") < 0.01
            && GetDouble(signals, "waveform.page_rate") > 2)
            return "Headless";

        if (GetDouble(signals, "waveform.burst_ratio") > 0.5) return "Bursty";

        if (GetBool(signals, "identity.is_new_fingerprint")
            && GetDouble(signals, "identity.match_score") < 0.5)
            return "New";

        return null;
    }

    private static bool GetBool(IReadOnlyDictionary<string, object?> signals, string key)
        => signals.TryGetValue(key, out var v) && v is bool b && b;
```

Remove the now-unused `GetBehaviorAdjective` and `GetToolNoun` helpers.

- [ ] **Step 2.2.2: Run all DeterministicBotNameTests, confirm pass**

Run: `dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~DeterministicBotNameTests"`
Expected: all tests PASS (both new and existing).

- [ ] **Step 2.2.3: Run the BDF integration rig, confirm no regression**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "Category=Integration&FullyQualifiedName~BdfReplayTests"`
Expected: PASS.

- [ ] **Step 2.2.4: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/DeterministicBotNameSynthesizer.cs
git commit -m "$(cat <<'EOF'
feat(naming): synthesizer becomes universal centroid + variance composer

GenerateName now follows four priorities: known UA bot name → matched
archetype name + variance → UA family + variance → 'analysing'. The
behaviour adjective/tool noun fallbacks that produced 'Automated Bot'
for humans are gone. Variance is a single decoration term computed from
existing signals (velocity, geo divergence, headless asset pattern,
burst ratio, new-with-low-match) and only appended when notable.

Naming is no longer a bot-only concern; the same pipeline names every
visitor. Hardcoded view-layer fallbacks ('Human', 'Unknown', etc.) are
removed in a follow-up.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase 3 - Remove the IsBot gates in `DetectionBroadcastMiddleware`

### Task 3.1: Drop IsBot gates at lines 172, 360, 378

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Middleware/DetectionBroadcastMiddleware.cs`

**Guardrail:** Line 387 (`UserAgent = result.IsBot ? Sanitize(...) : null`) is the PII zero-rule. Do NOT touch it. Lines 121/167 (attack arc broadcast) are cosmetic and out of scope - leave for a separate decision.

- [ ] **Step 3.1.1: Write a failing test asserting humans flow through TrackSignature**

In `src/Mostlylucid.BotDetection.UI.Test/Middleware/DetectionBroadcastMiddlewareTests.cs` (create if absent), add:

```csharp
[Fact]
public async Task HumanDetection_StillCallsTrackSignature()
{
    var mockTracker = new Mock<SignatureDescriptionService>(/* … */);
    var middleware = BuildMiddleware(tracker: mockTracker.Object);
    var ctx = BuildContext(isBot: false, primarySignature: "abc123", signals: new Dictionary<string, object> {
        ["identity.archetype_name"] = "Chrome on Windows"
    });

    await middleware.InvokeAsync(ctx, /* deps */);

    mockTracker.Verify(t => t.TrackSignature("abc123", It.IsAny<IReadOnlyDictionary<string, object?>>()), Times.Once);
}

[Fact]
public async Task HumanDetection_BroadcastsBotNameWhenPresent()
{
    var middleware = BuildMiddleware();
    var ctx = BuildContext(isBot: false, primaryBotName: "Chrome on Windows (US:abcd)");

    var stored = await CaptureStoredDetection(middleware, ctx);

    Assert.Equal("Chrome on Windows (US:abcd)", stored.BotName);
}
```

- [ ] **Step 3.1.2: Run tests, confirm fail**

Run: `dotnet test src/Mostlylucid.BotDetection.UI.Test --filter "FullyQualifiedName~DetectionBroadcastMiddlewareTests"`
Expected: tests FAIL - TrackSignature not called for non-bots; BotName is null.

- [ ] **Step 3.1.3: Drop the IsBot gate at line 172**

In `DetectionBroadcastMiddleware.cs`, replace:

```csharp
                if (signatureDescriptionService != null && detection.IsBot &&
                    !string.IsNullOrEmpty(detection.PrimarySignature) && evidence.Signals is { Count: > 0 })
```

with:

```csharp
                if (signatureDescriptionService != null &&
                    !string.IsNullOrEmpty(detection.PrimarySignature) && evidence.Signals is { Count: > 0 })
```

- [ ] **Step 3.1.4: Drop the IsBot gate at line 360**

Replace:

```csharp
        var botType = result.IsBot ? result.BotType?.ToString() : null;
```

with:

```csharp
        var botType = result.BotType?.ToString();
```

- [ ] **Step 3.1.5: Drop the IsBot gate at line 378**

Replace:

```csharp
            BotName = result.IsBot ? result.BotName : null,
```

with:

```csharp
            BotName = result.BotName,
```

- [ ] **Step 3.1.6: Confirm line 387 (PII gate) is still in place**

Verify the line still reads:

```csharp
            UserAgent = result.IsBot ? SanitizeUserAgent(context.Request.Headers.UserAgent.ToString()) : null,
```

This is the zero-PII rule from CLAUDE.md; it stays.

- [ ] **Step 3.1.7: Run tests, confirm pass**

Run: `dotnet test src/Mostlylucid.BotDetection.UI.Test --filter "FullyQualifiedName~DetectionBroadcastMiddlewareTests"`
Expected: PASS.

- [ ] **Step 3.1.8: Run full unit suite to catch downstream regressions**

Run: `dotnet test src/Mostlylucid.BotDetection.Test src/Mostlylucid.BotDetection.UI.Test`
Expected: PASS.

- [ ] **Step 3.1.9: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Middleware/DetectionBroadcastMiddleware.cs \
        src/Mostlylucid.BotDetection.UI.Test/Middleware/DetectionBroadcastMiddlewareTests.cs
git commit -m "$(cat <<'EOF'
feat(broadcast): humans flow through naming pipeline

Drops three IsBot gates that suppressed synthesizer invocation and
nulled BotName for non-bot detections. The raw-UA gate at line 387
stays (zero-PII rule). Combined with the variance-composer rewrite,
every visitor now carries a derived display name when broadcast and
persisted.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase 4 - Drop hardcoded view-layer fallbacks

### Task 4.1: Replace invented strings with the system-derived name

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbBadge/Default.cshtml`
- Modify: `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbSummary/Default.cshtml`
- Modify: `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbSummary/Card.cshtml`
- Modify: `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbThreatsList/Default.cshtml`
- Modify: `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/BotDetectionDetails/Default.cshtml`
- Modify: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SignatureDetail.cshtml`
- Keep: `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbSessionsList/Default.cshtml` (signature-hash fallback is correct for cold render before broadcast lands)

- [ ] **Step 4.1.1: SbBadge - drop the "Human" hardcode**

Replace:

```csharp
    var label = isBot ? (Model.BotName ?? Model.BotType ?? "Bot") : "Human";
```

with:

```csharp
    var label = Model.BotName ?? Model.BotType ?? (isBot ? "Bot" : "Visitor");
```

Rationale: BotName should always be present for any seen signature; the "Visitor"/"Bot" fallback only fires for the cold first paint.

- [ ] **Step 4.1.2: SbSummary Default - same fix**

Replace:

```csharp
    var label = Model.HasData ? (Model.IsBot ? (Model.BotName ?? Model.BotType ?? "Bot") : "Human") : "Unknown";
```

with:

```csharp
    var label = Model.HasData
        ? (Model.BotName ?? Model.BotType ?? (Model.IsBot ? "Bot" : "Visitor"))
        : "Unknown";
```

- [ ] **Step 4.1.3: SbSummary Card - same fix**

Replace:

```csharp
    var label = Model.HasData ? (isBot ? (Model.BotName ?? Model.BotType ?? "Bot") : "Human") : "Unknown";
```

with:

```csharp
    var label = Model.HasData
        ? (Model.BotName ?? Model.BotType ?? (isBot ? "Bot" : "Visitor"))
        : "Unknown";
```

- [ ] **Step 4.1.4: SbThreatsList - drop "Unknown" hardcode**

Replace:

```html
                                    <td class="text-xs">@(threat.BotName ?? "Unknown")</td>
```

with:

```html
                                    <td class="text-xs">@(threat.BotName ?? threat.PrimarySignature?[..Math.Min(12, threat.PrimarySignature.Length)] ?? "Unknown")</td>
```

Falls back to signature hash before the catch-all "Unknown".

- [ ] **Step 4.1.5: BotDetectionDetails - render unconditionally**

Replace:

```csharp
        @if (!string.IsNullOrEmpty(Model.BotName))
        {
            <span class="recommendation-bot"> | Identified: <strong>@Model.BotName</strong></span>
        }
```

with:

```csharp
        <span class="recommendation-bot"> | Identified: <strong>@(Model.BotName ?? Model.BotType ?? "Visitor")</strong></span>
```

- [ ] **Step 4.1.6: _SignatureDetail - drop "Signature" hardcode**

Replace:

```csharp
    <title>@(Model.Found ? (Model.BotName ?? "Signature") : "Not Found") - StyloBot</title>
```

with:

```csharp
    <title>@(Model.Found ? (Model.BotName ?? Model.PrimarySignature?[..Math.Min(12, Model.PrimarySignature.Length)] ?? "Signature") : "Not Found") - StyloBot</title>
```

- [ ] **Step 4.1.7: Run the demo, smoke-test the dashboard**

Run:

```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo
```

Visit `http://localhost:5080/SignatureDemo` from a real browser. Confirm:
- Signature badge does not say "Human"
- Sessions list shows the variance name (e.g. "Chrome on Windows") for the visitor
- No view crashes (null-ref) when BotName is absent on a fresh signature

Stop the demo: Ctrl+C.

- [ ] **Step 4.1.8: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbBadge/Default.cshtml \
        src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbSummary/Default.cshtml \
        src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbSummary/Card.cshtml \
        src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbThreatsList/Default.cshtml \
        src/Mostlylucid.BotDetection.UI/Views/Shared/Components/BotDetectionDetails/Default.cshtml \
        src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SignatureDetail.cshtml
git commit -m "$(cat <<'EOF'
refactor(dashboard): name visitors via system, not invented strings

The synthesizer now produces a display name for every visitor (bot or
human). The view layer no longer hardcodes 'Human' / 'Unknown' / 'Signature'
as conditional fallbacks. Where a fallback is still needed (cold first
paint before broadcast lands), prefer signature hash over an invented
word so the cold render is recognisable.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase 5 - Leiden → archetype bridge (separable follow-up)

**Context for the worker:** This phase closes the architectural split between Leiden clustering and identity archetypes. Today they're two parallel systems that share the word "cluster" but don't talk. After this phase, a mature Leiden cluster's member vectors define a new archetype centroid, the existing `ProcessClusterAsync` LLM prompt names it, and the new archetype joins `IdentityArchetypeRegistry` so future fingerprints can match against it. The variance composer from Phase 2 then picks up the LLM-given name automatically.

**The guardrail to test explicitly:** Two fingerprints that share an archetype must remain distinct fingerprints. The archetype is a *centroid for FindNearest*, not an identity merger. The `FingerprintMatchContributor` already doesn't merge identities just because they share a `client_type`; this phase must not break that. Task 5.4 asserts it directly.

### Task 5.1: Expose member identity vectors from `BotClusterService`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Services/BotClusterService.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Services/BotClusterServiceTests.cs` (extend)

- [ ] **Step 5.1.1: Define the new method shape**

In `BotClusterService.cs`, add:

```csharp
    /// <summary>
    ///     Returns each cluster member's identity vector (resolved via the slim signature
    ///     cache), filtered to members that actually have a vector available. Used by the
    ///     ArchetypeDiscoveryService to compute candidate archetype centroids.
    /// </summary>
    public IReadOnlyList<float[]> GetMemberIdentityVectors(string clusterId)
    {
        var cluster = FindClusterById(clusterId);
        if (cluster is null) return Array.Empty<float[]>();

        var vectors = new List<float[]>(cluster.MemberCount);
        foreach (var signatureId in cluster.MemberSignatures)
        {
            if (_signatureSearch.TryGetVector(signatureId, out var v))
                vectors.Add(v);
        }
        return vectors;
    }
```

Note: `FindClusterById` and `_signatureSearch.TryGetVector` may need to be added. Check the existing surface area before adding; reuse `FindCluster(string signature)` if it already does this lookup.

- [ ] **Step 5.1.2: Write a test asserting the method returns one vector per known member**

```csharp
[Fact]
public void GetMemberIdentityVectors_ReturnsOneVectorPerMember()
{
    var service = BuildServiceWithCluster("cluster-A", members: new[] { "sig1", "sig2", "sig3" });
    SeedSignatureVectors(("sig1", new float[]{1,2,3}), ("sig2", new float[]{4,5,6}), ("sig3", new float[]{7,8,9}));

    var vectors = service.GetMemberIdentityVectors("cluster-A");

    Assert.Equal(3, vectors.Count);
}

[Fact]
public void GetMemberIdentityVectors_SkipsMembersWithoutVector()
{
    var service = BuildServiceWithCluster("cluster-B", members: new[] { "sig1", "sig2" });
    SeedSignatureVectors(("sig1", new float[]{1,2,3})); // sig2 has no vector

    var vectors = service.GetMemberIdentityVectors("cluster-B");

    Assert.Single(vectors);
}
```

- [ ] **Step 5.1.3: Run, fix, commit**

Run: `dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~BotClusterServiceTests"`
Iterate until green, then commit:

```bash
git add src/Mostlylucid.BotDetection/Services/BotClusterService.cs \
        src/Mostlylucid.BotDetection.Test/Services/BotClusterServiceTests.cs
git commit -m "$(cat <<'EOF'
feat(clusters): BotClusterService exposes member identity vectors

Adds GetMemberIdentityVectors(clusterId) returning the identity-layout
vectors of cluster members that have one available. This is the input
to the ArchetypeDiscoveryService that promotes mature Leiden clusters
into named archetype centroids.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5.2: Create `ArchetypeDiscoveryService`

**Files:**
- Create: `src/Mostlylucid.BotDetection/Identity/ArchetypeDiscoveryService.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Identity/ArchetypeDiscoveryServiceTests.cs`

The service:
- Runs as a `BackgroundService` on a configurable interval (default 30 min)
- Iterates Leiden clusters; for each cluster not already represented by an archetype, checks the importance threshold (member count ≥ N, temporal density ≥ T, average similarity ≥ S - all configurable)
- For qualifying clusters, computes the centroid as the mean of member identity vectors
- Calls `LlmDescriptionCoordinator.EnqueueClusterAsync(clusterId, cluster, members)` - which already exists and writes back via the cluster callback. Reuse that callback signature; add an `ILlmResultCallback` overload `OnArchetypeDiscoveredAsync(archetypeId, centroid, name, description)` invoked by the discovery service after the LLM names the cluster.
- Calls `IdentityArchetypeRegistry.Upsert(...)` with the centroid + LLM name. Persists via `SqliteFingerprintStore.UpsertArchetypeAsync` (already exists for the calibration loop).

- [ ] **Step 5.2.1: Write the failing test for threshold gating**

```csharp
[Fact]
public async Task SmallCluster_DoesNotPromoteToArchetype()
{
    var discovery = BuildDiscovery(memberThreshold: 50);
    SeedCluster("tiny-cluster", memberCount: 10);

    await discovery.RunOnceAsync(CancellationToken.None);

    Assert.Empty(MockRegistry.UpsertedArchetypes);
}

[Fact]
public async Task MatureCluster_PromotesToArchetypeWithComputedCentroid()
{
    var discovery = BuildDiscovery(memberThreshold: 50);
    SeedCluster("mature", memberCount: 60, vectors: BuildKnownVectors());

    await discovery.RunOnceAsync(CancellationToken.None);

    var upserted = Assert.Single(MockRegistry.UpsertedArchetypes);
    Assert.Equal("mature", upserted.ArchetypeId);
    // Centroid should be the mean of seeded vectors
    AssertVectorEquals(upserted.Centroid, ExpectedMeanOfKnownVectors(), tolerance: 0.001f);
}
```

- [ ] **Step 5.2.2: Implement the service**

(Full implementation in the file. Skeleton below; the worker fills in the gaps.)

```csharp
public sealed class ArchetypeDiscoveryService : BackgroundService
{
    private readonly BotClusterService _clusters;
    private readonly IdentityArchetypeRegistry _registry;
    private readonly LlmDescriptionCoordinator _llm;
    private readonly SqliteFingerprintStore _store;
    private readonly ArchetypeDiscoveryOptions _opts;
    private readonly ILogger<ArchetypeDiscoveryService> _logger;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Discovery cycle failed"); }
            await Task.Delay(_opts.Interval, ct);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var cluster in _clusters.AllClusters)
        {
            if (_registry.TryGetById(cluster.ClusterId) is not null) continue;
            if (!IsMature(cluster)) continue;

            var vectors = _clusters.GetMemberIdentityVectors(cluster.ClusterId);
            if (vectors.Count < _opts.MinVectorsForCentroid) continue;

            var centroid = ComputeCentroid(vectors);
            await _llm.EnqueueClusterAsync(cluster.ClusterId, cluster, GetMemberBehaviors(cluster), ct);
            // The LLM coordinator callback wires through to the registry upsert.
        }
    }

    private bool IsMature(BotCluster c) =>
        c.MemberCount >= _opts.MinMembers
        && c.TemporalDensity >= _opts.MinTemporalDensity
        && c.AverageSimilarity >= _opts.MinAverageSimilarity;

    private static float[] ComputeCentroid(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0) throw new ArgumentException(nameof(vectors));
        var dim = vectors[0].Length;
        var sum = new float[dim];
        foreach (var v in vectors)
            for (var i = 0; i < dim; i++) sum[i] += v[i];
        for (var i = 0; i < dim; i++) sum[i] /= vectors.Count;
        return sum;
    }
}
```

- [ ] **Step 5.2.3: Wire the LLM callback back to `IdentityArchetypeRegistry.Upsert`**

In the existing `LlmDescriptionCoordinator.ProcessClusterAsync` (line 136-169), after the existing `_clusterService.UpdateClusterDescription` call, add an archetype-upsert call if `IdentityArchetypeRegistry` is registered:

```csharp
        _archetypeUpsert?.Invoke(
            req.Cluster.ClusterId,
            ComputeCentroidForCluster(req.Cluster, req.ClusterMembers),
            result.Value.Name,
            result.Value.Description);
```

Inject the upsert hook through the constructor (`Action<string, float[], string, string>? archetypeUpsert = null`), wired in `ServiceCollectionExtensions` only when identity is enabled.

- [ ] **Step 5.2.4: Run tests, commit**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~ArchetypeDiscoveryServiceTests"
```

Commit:

```bash
git add src/Mostlylucid.BotDetection/Identity/ArchetypeDiscoveryService.cs \
        src/Mostlylucid.BotDetection/Services/LlmDescriptionCoordinator.cs \
        src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs \
        src/Mostlylucid.BotDetection.Test/Identity/ArchetypeDiscoveryServiceTests.cs
git commit -m "$(cat <<'EOF'
feat(identity): ArchetypeDiscoveryService promotes Leiden clusters

Closes the architectural split between Leiden clustering and identity
archetypes. Mature clusters (member count + temporal density + average
similarity thresholds) get their member-vector mean computed as a
candidate centroid, the existing LLM cluster-naming prompt names it,
and the result upserts into IdentityArchetypeRegistry. Future
fingerprints can now match against LLM-discovered archetypes via the
same FindNearest path that handles the YAML-seeded ones.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5.3: Tag discovered archetypes with provenance

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityArchetype.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityArchetypeRegistry.cs` (so calibration knows which archetypes it owns)

- [ ] **Step 5.3.1: Confirm `IdentityArchetype` has an `ArchetypeKind` field**

Read `IdentityArchetype.cs`. If `ArchetypeKind` exists, ensure it's settable on `Upsert`. If not, add it:

```csharp
    public string? ArchetypeKind { get; init; }
```

- [ ] **Step 5.3.2: Tag discovered archetypes with `"leiden-discovered"`**

In `ArchetypeDiscoveryService`, when constructing the archetype to upsert, set `ArchetypeKind = "leiden-discovered"`. The calibration loop's archetype-refinement step (`IdentityWeightCalibrationService.RefineArchetypeCentroid`) treats all archetypes uniformly today - that's fine, the calibration *can* refine discovered archetypes too. The tag is for debug/audit, not gate logic.

- [ ] **Step 5.3.3: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/IdentityArchetype.cs \
        src/Mostlylucid.BotDetection/Identity/ArchetypeDiscoveryService.cs
git commit -m "$(cat <<'EOF'
chore(identity): tag discovered archetypes with provenance

Sets ArchetypeKind = 'leiden-discovered' on archetypes promoted from
Leiden clusters, so the calibration service and admin tooling can tell
them apart from YAML-seeded ones at a glance.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5.4: Guardrail test - fingerprints stay distinct within an archetype

**Files:**
- Create: `src/Mostlylucid.BotDetection.Test/Identity/ArchetypeDoesNotMergeFingerprintsTests.cs`

- [ ] **Step 5.4.1: Write the assertion**

```csharp
public class ArchetypeDoesNotMergeFingerprintsTests
{
    [Fact]
    public async Task NearlyIdenticalFingerprints_RemainDistinct_WhenSharingArchetype()
    {
        // Two fingerprints with the same archetype match but distinct enough vectors
        // to be different fingerprints - typical of two scrapers from different IPs.
        var harness = BuildIdentityHarness();
        var archetypeId = "python-requests";

        var fp1 = await harness.MatchAsync(BuildVectorNear(archetypeId, jitter: 0.05f));
        var fp2 = await harness.MatchAsync(BuildVectorNear(archetypeId, jitter: 0.05f, seed: 2));

        Assert.NotEqual(fp1.FingerprintId, fp2.FingerprintId);
        Assert.Equal(archetypeId, fp1.InferredClientType);
        Assert.Equal(archetypeId, fp2.InferredClientType);
    }
}
```

- [ ] **Step 5.4.2: Confirm the test passes against the existing matcher (no production change needed)**

Run: `dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~ArchetypeDoesNotMergeFingerprintsTests"`
Expected: PASS without modifying production code. The matcher's L1/L2 logic already keeps fingerprints distinct; this test pins the contract.

If it fails, it means a prior step (likely the Phase 5 upsert) tightened things incorrectly. Stop and diagnose - do not loosen the assertion to make it pass.

- [ ] **Step 5.4.3: Commit**

```bash
git add src/Mostlylucid.BotDetection.Test/Identity/ArchetypeDoesNotMergeFingerprintsTests.cs
git commit -m "$(cat <<'EOF'
test(identity): pin the 'archetype is not an identity merger' contract

Asserts that two fingerprints sharing an archetype remain distinct
fingerprints. Catches regressions where Leiden-cluster promotion or
archetype refinement starts collapsing distinct identities into one,
which would be a worse failure mode than the current 'no archetype'
state.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Acceptance criteria

After all phases:

1. A first-time human visitor with Chrome on Windows appears in the dashboard as `"Chrome on Windows (US:abcd)"` (or similar), not as `"Human"`, `"Unknown"`, or a bare signature hash.
2. A bot family that already had a YAML archetype (e.g. python-requests scraper) is named via the archetype, not via the legacy `"Automated Bot"` synthesis branch.
3. A fingerprint that's rotating IPs has `"Rotating"` appended to its archetype name.
4. A fingerprint whose geo diverges from the archetype's dominant country has `"Travelling"` appended.
5. After running with Leiden discovery enabled and accumulating enough cluster mass, new LLM-named archetypes appear in `fingerprints.db` `identity_archetypes` table, and future fingerprints from those clusters inherit the LLM-given name.
6. Two distinct python-requests scrapers from different IPs both display as `"python-requests"` (no variance), but remain distinct rows in the fingerprint store.
7. `git grep '"Human"' src/Mostlylucid.BotDetection.UI/Views` returns no hits.
8. The BDF integration rig (`BdfReplayTests.Integration.cs`) still passes for all bot and human scenarios.

---

## Self-review notes

**Spec coverage:** Phases 1–4 deliver the "name humans via the same system" goal. Phase 5 delivers the self-improving loop. The "fingerprints stay distinct within a shared archetype" guardrail is pinned by Task 5.4. The "replace hardcoded nonsense" requirement is Phase 4. The "LLM names centroids, deterministic adds variance" architecture is Phases 1+2 (synthesizer reads archetype name) and Phase 5 (LLM names archetype centroids).

**Placeholders:** Every step shows the actual code or command. The `BuildHarness`, `BuildRegistry`, `BuildIdentityHarness` helpers in tests are conventional - the worker should follow existing test patterns in the same test file's namespace. If a step says "use mock", a real Moq or fake stub is intended; the worker writes the boilerplate per project convention.

**Type consistency:** `IdentityArchetypeName` (line 1.1.1) is read by `GenerateName` in Task 2.2 and written by `FingerprintMatchContributor` in Task 1.3 - same constant. `TryGetById` (Task 1.2) is called from Task 1.3 - same signature `(string archetypeId) → IdentityArchetype?`. `GetMemberIdentityVectors(clusterId)` (Task 5.1) is called from `ArchetypeDiscoveryService.RunOnceAsync` (Task 5.2) - same signature.

**Open question for the worker:** Phase 5 Task 5.2.3 wires the LLM callback through an `Action<...>` hook. This is the smallest-blast-radius change to `LlmDescriptionCoordinator`. If the codebase prefers a typed `IArchetypeCallback` interface (consistent with `IClusterDescriptionCallback`), switch to that - the test in 5.2.1 only asserts the upsert happens, not how it's wired.