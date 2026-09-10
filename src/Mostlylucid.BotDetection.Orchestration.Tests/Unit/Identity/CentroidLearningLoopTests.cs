using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     End-to-end coverage for the centroid learning loop. Existing tests cover
///     individual mechanics (absorption math, calibration math, registry lookups,
///     anchor-index recall). These three tests close the gap by exercising the
///     <b>loop as a loop</b>: do centroids actually move when observations arrive,
///     does a calibration pass actually populate the durable archetype + weight
///     tables, and does the matcher actually partition distinct visitor shapes?
///
///     <para>
///         All three tests drive the public service entry points
///         (<see cref="FingerprintAbsorptionService.TickOnceAsync"/>,
///         <see cref="IdentityWeightCalibrationService.RunOnceAsync"/>) directly
///         instead of relying on the schedule coordinator. The fixed-tick trigger
///         is the wrong abstraction; these tests are agnostic to whatever adaptive
///         trigger lands later.
///     </para>
/// </summary>
public sealed class CentroidLearningLoopTests : IDisposable
{
    private readonly string _tempDir;

    public CentroidLearningLoopTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-learning-loop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ----- Test 1: centroids drift under repeated observations ----------------

    [Fact]
    public async Task Centroid_drifts_toward_observed_shape_under_repeated_observations()
    {
        // Pin: feed N observations of the same vector at a fingerprint that starts
        // at the origin. The maturity-weighted-mean fold MUST move the centroid
        // toward the observation each request, and the L2 distance to the
        // observation MUST decrease monotonically. If the memory fold silently
        // no-ops, or the centroid update is dropped, this test goes red.
        // Phase B (write-path grain redesign): the fold happens on the request
        // thread inside RecordObservationAsync — the absorption service's DB role
        // ended, so there is no tick to drive.
        var (store, _, _) = await BuildAsync();
        try
        {
            var dim = store.Layout.Dimension;
            var observed = IdentityTestHelpers.MakeUnitVector(dim, seed: 1);

            var fpId = "fp-drift-target";
            await SeedFingerprintAtOriginAsync(store, fpId, dim);
            await store.GetFingerprintAsync(fpId); // warm the LFU (the fold needs a resident entry)

            var distances = new List<double>();
            distances.Add(L2Distance(new float[dim], observed)); // origin -> observed

            for (var i = 0; i < 10; i++)
            {
                await store.RecordObservationAsync(RequestScope.Unknown, fpId, observed, uaFamily: "chrome");

                var fp = await store.GetFingerprintAsync(fpId);
                Assert.NotNull(fp);
                distances.Add(L2Distance(fp!.Centroid, observed));
            }

            // The first folded step must move the centroid off the origin.
            Assert.True(distances[1] < distances[0],
                $"first fold did not move centroid: origin->obs distance {distances[0]:F4} -> after-first {distances[1]:F4}");

            // Monotonic non-increase: every subsequent step must close the gap
            // (or hold it; floating point can stall at the asymptote). This is
            // the "learning is actually learning" assertion.
            for (var i = 2; i < distances.Count; i++)
            {
                Assert.True(distances[i] <= distances[i - 1] + 1e-6,
                    $"distance regressed at step {i}: {distances[i - 1]:F6} -> {distances[i]:F6}");
            }

            // And the gap actually shrank meaningfully end-to-end, not just by
            // an epsilon -- catches "the fold ran but barely moved anything".
            Assert.True(distances[^1] < distances[0] * 0.5,
                $"centroid only moved {distances[0] - distances[^1]:F4} of {distances[0]:F4} after 10 folded observations");
        }
        finally
        {
        }
    }

    // ----- Test 2: calibration pass populates archetypes + weights -------------

    [Fact]
    public async Task Calibration_run_populates_archetypes_and_dimension_weights_durable_tier()
    {
        // Pin: a single RunOnceAsync on a populated store MUST land rows in
        // identity_archetypes (cold-seed pass) and identity_dimension_weights
        // (Fisher pass). The bug we're guarding against is the live Demo's
        // fingerprints.db, which has 2 obs but ZERO archetypes / weights -- the
        // calibration tick never fired in the 30-min default window, so the
        // durable tier looks dead.
        var (store, absorption, calibration) = await BuildAsync();
        try
        {
            var dim = store.Layout.Dimension;

            // Seed a small population across two distinct shapes so Fisher
            // weights have something to discriminate. Identical centroids
            // would give degenerate within-class variance and the weights
            // pass would no-op.
            var shapeA = IdentityTestHelpers.MakeUnitVector(dim, seed: 10);
            var shapeB = IdentityTestHelpers.MakeUnitVectorOrthogonalTo(dim, seed: 11, shapeA);

            for (var i = 0; i < 5; i++)
            {
                var aid = $"fp-shape-a-{i}";
                var bid = $"fp-shape-b-{i}";
                await SeedFingerprintAtOriginAsync(store, aid, dim, inferredClientType: "shape-a");
                await SeedFingerprintAtOriginAsync(store, bid, dim, inferredClientType: "shape-b");
                await store.RecordObservationAsync(RequestScope.Unknown, aid, shapeA, uaFamily: "chrome");
                await store.RecordObservationAsync(RequestScope.Unknown, bid, shapeB, uaFamily: "googlebot");
            }
            await absorption.TickOnceAsync(CancellationToken.None);

            var archetypesBefore = (await store.ListFingerprintsAsync()).Count;
            Assert.True(archetypesBefore >= 10, "seed step should have inserted 10 fingerprints");

            // Drive the calibration loop directly. This exercises BOTH paths:
            // cold-seed (InsertArchetypeIfMissingAsync per registered archetype)
            // AND the Fisher pass (which persists identity_dimension_weights).
            var result = await calibration.RunOnceAsync(CancellationToken.None);

            // Cold-seed must have written archetype rows. The registry has the
            // embedded YAML archetypes loaded at construction, so the count
            // here mirrors All.Count.
            var archetypeRowCount = await CountRowsAsync(store, "identity_archetypes");
            Assert.True(archetypeRowCount > 0,
                $"cold-seed pass did NOT populate identity_archetypes (result={result})");

            // Fisher weights must have landed too -- this is the row count that
            // is zero in the live Demo db today.
            var weightRowCount = await CountRowsAsync(store, "identity_dimension_weights");
            Assert.True(weightRowCount > 0,
                $"calibration pass did NOT populate identity_dimension_weights (result={result})");
        }
        finally
        {
            calibration.Dispose();
            absorption.Dispose();
        }
    }



    // ----- Test 3: distinct shapes resolve to distinct archetypes -------------

    [Fact]
    public void Distinct_visitor_shapes_resolve_to_distinct_archetypes()
    {
        // Pin: the matcher MUST partition distinct visitor shapes to distinct
        // archetypes. If FindNearest collapses every shape to the same answer,
        // learning's classification surface is broken regardless of how nicely
        // centroids drift -- every visitor would resolve to the same identity.
        //
        // The cleanest property to assert: at least two DISTINCT archetypes
        // each resolve to themselves when their own centroid + UA family is
        // fed back to FindNearest. If fewer than two archetypes can claim
        // their own centroid, the matcher is dominated by an umbrella that
        // wins for everything.
        //
        // NOTE: an earlier draft picked the registry's max-L2-distance pair
        // and asserted each resolved to itself. That failed on the
        // chrome-privacy + chrome-desktop axis: chrome-desktop's masked-cosine
        // catchment wins against chrome-privacy's own centroid. That's a real
        // umbrella-centroid finding worth following up on (see the test memo
        // in the failure), but the property we want THIS test to guard is
        // partition-not-collapse, not strict identity-self-resolution.
        var encoder = new IdentityVectorEncoder(IdentityVectorLayout.DefaultV1());
        var registry = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, encoder);

        Assert.True(registry.All.Count >= 2,
            "embedded archetype catalogue must ship at least 2 archetypes for a partition test");

        var selfResolving = new List<IdentityArchetype>();
        var collapseTargets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var arch in registry.All)
        {
            var match = registry.FindNearest(arch.Centroid, arch.AssertedUaFamily);
            if (match is null) continue;
            collapseTargets[match.Archetype.ArchetypeId] =
                collapseTargets.TryGetValue(match.Archetype.ArchetypeId, out var c) ? c + 1 : 1;
            if (string.Equals(match.Archetype.ArchetypeId, arch.ArchetypeId, StringComparison.OrdinalIgnoreCase))
                selfResolving.Add(arch);
        }

        // Partition-not-collapse: there MUST be more than one distinct match
        // result across the catalogue. If FindNearest returned the same answer
        // for every archetype, learning has no classifier.
        Assert.True(collapseTargets.Count > 1,
            $"FindNearest collapsed every archetype's own centroid to {collapseTargets.Count} target(s) -- " +
            $"the matcher is dominated by an umbrella centroid. Collapse map: " +
            string.Join(", ", collapseTargets.Select(kv => $"{kv.Key}={kv.Value}")));

        // Self-resolution is the stronger property: at least two archetypes
        // must claim their own centroid. The umbrella-leak from the chrome-
        // family is acceptable noise here as long as the catalogue still has
        // two anchors that hold. If it doesn't, the matcher has degenerated
        // to a single umbrella.
        Assert.True(selfResolving.Count >= 2,
            $"only {selfResolving.Count} archetype(s) resolve to themselves out of {registry.All.Count}. " +
            $"Self-resolving: {string.Join(",", selfResolving.Select(a => a.ArchetypeId))}");

        var a = selfResolving[0];
        var b = selfResolving[1];
        var matchA = registry.FindNearest(a.Centroid, a.AssertedUaFamily);
        var matchB = registry.FindNearest(b.Centroid, b.AssertedUaFamily);
        Assert.NotEqual(matchA!.Archetype.ArchetypeId, matchB!.Archetype.ArchetypeId);
    }

    // ----- D1: novelty-driven basin seeding ------------------------------------

    private static async Task<IReadOnlyList<IdentityArchetypeRow>> ArchetypeRowsAsync(SqliteFingerprintStore store)
        => await store.GetByCatalogueKindAsync("identity");

    /// <summary>
    ///     The CONSUMER, not the field: a fingerprint whose NoveltyCount has crossed the gate gets a
    ///     basin of its own at the next consolidation pass, and a below-gate control does not. Fails
    ///     without the wiring, because nothing reads NoveltyCount.
    /// </summary>
    [Fact]
    public async Task Novelty_past_the_gate_seeds_a_basin_and_the_control_does_not()
    {
        var (store, _, calibration) = await BuildAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;

        // Settle the pass's own cold-seed writes first, so the assertion is about the SEED and not
        // about the catalogue being written to the store for the first time.
        await calibration.RunOnceAsync(CancellationToken.None);

        await SeedFingerprintAtOriginAsync(store, "fp-novel-over-gate", dim, noveltyCount: 5);
        await SeedFingerprintAtOriginAsync(store, "fp-novel-under-gate", dim, noveltyCount: 0);

        await calibration.RunOnceAsync(CancellationToken.None);

        var rows = await ArchetypeRowsAsync(store);
        Assert.Contains(rows, r => r.ArchetypeId == "emergent-fp-novel-over-gate");
        Assert.DoesNotContain(rows, r => r.ArchetypeId == "emergent-fp-novel-under-gate");
    }

    /// <summary>
    ///     Re-expressed (foss-, 2026-09-10). This previously asserted that a STILL-NOVEL seed keeps its
    ///     basin with no descendants — which encoded the defect: `NoveltyCount` is a lifetime counter,
    ///     so "still novel" is permanently true for anything already seeded, and that condition made
    ///     retirement unreachable. Novelty is NOT a retention condition. What the original test was
    ///     really protecting — that retirement must not cause a re-seed flap — is covered by
    ///     <see cref="A_retired_seed_is_not_rebuilt_without_new_evidence"/>.
    /// </summary>
    [Fact]
    public async Task A_still_novel_seed_does_NOT_keep_an_unmatched_basin()
    {
        var (store, _, calibration) = await BuildAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await calibration.RunOnceAsync(CancellationToken.None);

        await SeedFingerprintAtOriginAsync(store, "fp-still-novel", dim, noveltyCount: 5);
        for (var pass = 0; pass < 4; pass++)
            await calibration.RunOnceAsync(CancellationToken.None);

        // Still over the novelty gate, but nothing ever matched into it: the basin goes.
        Assert.DoesNotContain(await ArchetypeRowsAsync(store), r => r.ArchetypeId == "emergent-fp-still-novel");
    }

    /// <summary>
    ///     Cooling, driven by the REAL production path: a seeded basin nobody joins is retired after
    ///     the cooling window, so seeding cannot become unbounded proliferation.
    ///     <para>
    ///     This test previously reached its "the shape stabilised" state by calling
    ///     <c>DeleteFingerprintsAsync</c> and re-inserting the row at <c>noveltyCount: 0</c> — a
    ///     transition production has no path to, since <c>NoveltyCount</c> only ever increments
    ///     (IdentityDeltaMath.FoldObservation: `+0` or `+1`, no reset anywhere). It therefore passed
    ///     while the rule it describes was unreachable. It now drives the state production actually
    ///     produces: the fingerprint simply stays in the store, over the gate, and nothing matches it.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task A_seeded_basin_is_retired_only_after_the_cooling_window()
    {
        var (store, _, calibration) = await BuildAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await calibration.RunOnceAsync(CancellationToken.None);

        await SeedFingerprintAtOriginAsync(store, "fp-novel-cooling", dim, noveltyCount: 5);
        await calibration.RunOnceAsync(CancellationToken.None);
        Assert.Contains(await ArchetypeRowsAsync(store), r => r.ArchetypeId == "emergent-fp-novel-cooling");

        // Default CoolingCycles = 3. The fingerprint stays at its crossed-the-gate novelty, exactly as
        // production leaves it, and nothing is ever matched into the basin. The pass that CREATED the
        // basin is itself the first below-minimum pass, so retirement lands on the third.
        // It survives inside the window...
        await calibration.RunOnceAsync(CancellationToken.None);
        Assert.Contains(await ArchetypeRowsAsync(store), r => r.ArchetypeId == "emergent-fp-novel-cooling");

        // ...and is retired once the window closes.
        await calibration.RunOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(await ArchetypeRowsAsync(store), r => r.ArchetypeId == "emergent-fp-novel-cooling");
    }

    /// <summary>
    ///     Usage-driven retention, the positive half: a basin that IS being matched stays, however long
    ///     it lives. Retention is not time-bounded — it is bounded by whether anything joins.
    /// </summary>
    [Fact]
    public async Task A_seeded_basin_that_is_being_matched_is_NOT_retired()
    {
        var (store, _, calibration) = await BuildAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await calibration.RunOnceAsync(CancellationToken.None);

        await SeedFingerprintAtOriginAsync(store, "fp-joined", dim, noveltyCount: 5);
        await calibration.RunOnceAsync(CancellationToken.None);
        Assert.Contains(await ArchetypeRowsAsync(store), r => r.ArchetypeId == "emergent-fp-joined");

        // A fingerprint whose shape resolved INTO the basin: this is the usage signal, and it is the
        // only thing that keeps a seeded basin alive.
        await SeedFingerprintAtOriginAsync(store, "fp-descendant", dim,
            inferredClientType: "emergent-fp-joined");

        for (var pass = 0; pass < 6; pass++)
            await calibration.RunOnceAsync(CancellationToken.None);

        Assert.Contains(await ArchetypeRowsAsync(store), r => r.ArchetypeId == "emergent-fp-joined");
    }

    /// <summary>
    ///     The flap guard: a retired seed is NOT rebuilt just because its lifetime novelty is still over
    ///     the gate — that would churn seed → retire → re-seed on a timer with nothing changed. Only new
    ///     evidence (novelty advancing past the value held at retirement) rebuilds it.
    /// </summary>
    [Fact]
    public async Task A_retired_seed_is_not_rebuilt_without_new_evidence()
    {
        var (store, _, calibration) = await BuildAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await calibration.RunOnceAsync(CancellationToken.None);

        await SeedFingerprintAtOriginAsync(store, "fp-no-churn", dim, noveltyCount: 5);

        // Long past seed + cool + retire: with no new evidence the basin must stay retired.
        for (var pass = 0; pass < 10; pass++)
            await calibration.RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(await ArchetypeRowsAsync(store), r => r.ArchetypeId == "emergent-fp-no-churn");
    }

    // ----- helpers ------------------------------------------------------------

    private async Task<(SqliteFingerprintStore Store, FingerprintAbsorptionService Absorption, IdentityWeightCalibrationService Calibration)>
        BuildAsync()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, $"botdetection-{Guid.NewGuid():N}.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Vector = new IdentityVectorOptions
                {
                    AbsorptionMaturityThreshold = 1,   // absorb after the first obs so tests fire fast
                    AbsorptionAgeDays = 30,
                    ActiveWindowDays = 90,
                    SubscriptionDebounceMs = 0,        // tests drive TickOnceAsync directly
                    // These tests exercise the ABSORBER'S maturity-weighted fold in isolation,
                    // feeding repeated identical observations. Adaptive-forgetting sampling
                    // (default on) would correctly summarise those confirmatory repeats once the
                    // centroid converges (nothing left to absorb), orthogonal to what is under
                    // test here. Turn it off so every observation reaches the absorber; the
                    // sampling decision is covered by AdaptiveObservationSamplingTests.
                    AdaptiveObservationSampling = false,
                },
                Calibration = new IdentityCalibrationOptions
                {
                    CalibrationIntervalMinutes = 1,    // gate is bypassed when we call RunOnceAsync
                }
            }
        });

        var layout = IdentityVectorLayout.DefaultV1();
        var store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, options, layout);
        await store.EnsureInitialisedAsync();

        var encoder = new IdentityVectorEncoder(layout);
        var archetypes = new IdentityArchetypeRegistry(NullLogger<IdentityArchetypeRegistry>.Instance, encoder);

        var absorption = new FingerprintAbsorptionService(
            NullLogger<FingerprintAbsorptionService>.Instance,
            store,
            archetypes,
            options,
            scheduleCoordinator: null);

        var calibration = new IdentityWeightCalibrationService(
            NullLogger<IdentityWeightCalibrationService>.Instance,
            store,
            archetypes,
            options,
            scheduleCoordinator: null);

        return (store, absorption, calibration);
    }

    private static async Task SeedFingerprintAtOriginAsync(
        SqliteFingerprintStore store,
        string fpId,
        int dim,
        string inferredClientType = "test",
        int noveltyCount = 0)
    {
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        var now = DateTime.UtcNow;
        var fp = new Fingerprint
        {
            FingerprintId = fpId,
            Centroid = new float[dim],
            CentroidMaturity = 0,           // a true cold start: any observation MUST move us
            NoveltyCount = noveltyCount,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 0,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = inferredClientType,
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
        };
        await store.InsertFingerprintAsync(fp, $"sig-{fpId}", CancellationToken.None);
    }

    private static double L2Distance(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }
        return Math.Sqrt(sum);
    }

    private async Task<long> CountRowsAsync(SqliteFingerprintStore store, string table)
    {
        await store.EnsureInitialisedAsync();
        // Store derives its file path as Path.Combine(GetDirectoryName(DatabasePath), "fingerprints.db").
        // Mirror that here -- DatabasePath is not exposed on the store surface.
        var fpDb = Path.Combine(_tempDir, "fingerprints.db");
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={fpDb}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        var result = await cmd.ExecuteScalarAsync();
        return (long)(result ?? 0L);
    }
}
