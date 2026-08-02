using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     2026-08-02 fp-cache-current architecture, Task 14: <see cref="FingerprintDriftService"/>'s
///     weighted-cosine BEHAVIOURAL-shape drift (current identity vector vs. the fingerprint's
///     established centroid+weights) now feeds scoring in REAL TIME, per request, as a
///     <see cref="DetectionContribution"/> -- mirroring the pattern <see cref="IdentityChangeAtom"/>
///     already uses for surface-dims + drift-frequency, but computed inline against THIS
///     request's own <see cref="IdentityVectorAtom"/>-composed vector rather than a
///     stored "latest observation" from a background pass. Distinct from the surface-dims
///     comparison (geo/ASN/UA/canvas): this catches drift even when every discrete surface dim
///     stays put -- the "Adblocker -&gt; curl" case (same IP/UA/geo, different tool shape).
/// </summary>
public sealed class IdentityChangeBehavioralDriftTests
{
    private const string Session = "session-1";
    private const string FingerprintId = "fp-behavioral";
    private static readonly int Dim = IdentityVectorLayout.DefaultV1().Dimension;

    private static SignalSink NewSink()
    {
        var sink = new SignalSink(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));
        sink.Raise($"{SignalKeys.IdentityFingerprintId}:{FingerprintId}", Session);
        return sink;
    }

    private static DefaultHttpContext ContextWithVector(float[]? vector)
    {
        var context = new DefaultHttpContext();
        if (vector is not null)
            context.Items[IdentityVectorAtom.VectorKey] = vector;
        return context;
    }

    private static IdentityChangeAtom NewAtom(IFingerprintStore store, HttpContext context) => new(
        NullLogger<IdentityChangeAtom>.Instance,
        new StubDetectorConfigProvider(),
        store,
        new StaticHttpContextAccessor(context),
        new IdentityGlobalWeightsCache(
            NullLogger<IdentityGlobalWeightsCache>.Instance,
            store,
            Options.Create(new BotDetectionOptions { Identity = new IdentityOptions { Enabled = true } })));

    private static Fingerprint Fixture(
        float[] centroid, int centroidMaturity, double cachedBotProbability = 0.0) => new()
    {
        FingerprintId = FingerprintId,
        Centroid = centroid,
        CentroidMaturity = centroidMaturity,
        Weights = Ones(),
        MemberCount = 1,
        ObservationCount = centroidMaturity,
        CorrectionCount = 0,
        FirstSeen = DateTime.UtcNow.AddDays(-1),
        LastSeen = DateTime.UtcNow,
        Quality = 0.9,
        InferredClientType = "chrome-desktop",
        InferredTypeConfidence = 0.9,
        InferredTypeChangedAt = DateTime.UtcNow.AddDays(-1),
        CachedBotProbability = cachedBotProbability
    };

    private static float[] Ones()
    {
        var w = new float[Dim];
        Array.Fill(w, 1.0f);
        return w;
    }

    private static float[] AxisVector(int axis)
    {
        var v = new float[Dim];
        v[axis] = 1.0f;
        return v;
    }

    private sealed class FakeStore : NullFingerprintStore
    {
        private readonly Fingerprint? _fp;
        public FakeStore(Fingerprint? fp) => _fp = fp;
        public override Task<Fingerprint?> GetFingerprintAsync(string fingerprintId, CancellationToken ct = default)
            => Task.FromResult(fingerprintId == FingerprintId ? _fp : null);
    }

    [Fact]
    public async Task CurrentVectorOrthogonalToCentroid_RaisesBehavioralDriftContribution()
    {
        // Established centroid points along axis 0; this request's vector points along axis 1
        // -- orthogonal, weighted-cosine == 0, deterministically below any warning threshold.
        var store = new FakeStore(Fixture(AxisVector(0), centroidMaturity: 50));
        var context = ContextWithVector(AxisVector(1));
        var atom = NewAtom(store, context);

        var result = await atom.DetectAsync(NewSink(), Session);

        Assert.Contains(result, c => c.Category == "BehavioralDrift");
        var drift = Assert.Single(result, c => c.Category == "BehavioralDrift");
        Assert.True(drift.ConfidenceDelta > 0.0, "behavioral drift contribution must carry positive confidence");
    }

    [Fact]
    public async Task CurrentVectorMatchesCentroid_RaisesNoBehavioralDriftContribution()
    {
        // Identical shape -- weighted-cosine == 1.0, well above the warning threshold.
        var store = new FakeStore(Fixture(AxisVector(0), centroidMaturity: 50));
        var context = ContextWithVector(AxisVector(0));
        var atom = NewAtom(store, context);

        var result = await atom.DetectAsync(NewSink(), Session);

        Assert.DoesNotContain(result, c => c.Category == "BehavioralDrift");
    }

    [Fact]
    public async Task NoCentroidMaturityYet_SkipsBehavioralDriftCheck()
    {
        // Cold-start fingerprint (no observations absorbed into the centroid yet) -- comparing
        // against an all-zero centroid would be a meaningless, guaranteed-below-threshold read;
        // must not fire a false positive on every brand-new visitor.
        var store = new FakeStore(Fixture(new float[Dim], centroidMaturity: 0));
        var context = ContextWithVector(AxisVector(1));
        var atom = NewAtom(store, context);

        var result = await atom.DetectAsync(NewSink(), Session);

        Assert.DoesNotContain(result, c => c.Category == "BehavioralDrift");
    }

    [Fact]
    public async Task NoCurrentVectorComposedThisRequest_SkipsBehavioralDriftCheck()
    {
        // IdentityVectorAtom didn't run / didn't compose a vector this request (e.g. Identity
        // disabled) -- nothing to compare, must not throw and must not fire.
        var store = new FakeStore(Fixture(AxisVector(0), centroidMaturity: 50));
        var context = ContextWithVector(vector: null);
        var atom = NewAtom(store, context);

        var result = await atom.DetectAsync(NewSink(), Session);

        Assert.DoesNotContain(result, c => c.Category == "BehavioralDrift");
    }

    // ========================================================================
    // Loop-guard #1 (2026-08-02, operator hard guardrail): drift must be measured
    // from behavioral SHAPE, never from the score. Varying CachedBotProbability
    // while holding the vector/centroid fixed must not change the drift output
    // at all -- the atom has no read access to the score in its drift branch, so
    // this pins that isolation explicitly rather than relying on "it just doesn't
    // read the field".
    // ========================================================================

    [Fact]
    public async Task DriftContribution_IsIdenticalRegardlessOfCachedBotProbability()
    {
        var lowScoreStore = new FakeStore(Fixture(AxisVector(0), centroidMaturity: 50, cachedBotProbability: 0.05));
        var highScoreStore = new FakeStore(Fixture(AxisVector(0), centroidMaturity: 50, cachedBotProbability: 0.95));
        var vector = AxisVector(1); // orthogonal -> fires drift identically either way

        var lowScoreResult = await NewAtom(lowScoreStore, ContextWithVector(vector)).DetectAsync(NewSink(), Session);
        var highScoreResult = await NewAtom(highScoreStore, ContextWithVector(vector)).DetectAsync(NewSink(), Session);

        var lowDrift = Assert.Single(lowScoreResult, c => c.Category == "BehavioralDrift");
        var highDrift = Assert.Single(highScoreResult, c => c.Category == "BehavioralDrift");
        Assert.Equal(lowDrift.ConfidenceDelta, highDrift.ConfidenceDelta, precision: 9);
    }

    [Fact]
    public async Task NoDriftContribution_RegardlessOfCachedBotProbability_WhenVectorMatchesCentroid()
    {
        var lowScoreStore = new FakeStore(Fixture(AxisVector(0), centroidMaturity: 50, cachedBotProbability: 0.05));
        var highScoreStore = new FakeStore(Fixture(AxisVector(0), centroidMaturity: 50, cachedBotProbability: 0.95));
        var vector = AxisVector(0); // matches centroid -> no drift either way

        var lowScoreResult = await NewAtom(lowScoreStore, ContextWithVector(vector)).DetectAsync(NewSink(), Session);
        var highScoreResult = await NewAtom(highScoreStore, ContextWithVector(vector)).DetectAsync(NewSink(), Session);

        Assert.DoesNotContain(lowScoreResult, c => c.Category == "BehavioralDrift");
        Assert.DoesNotContain(highScoreResult, c => c.Category == "BehavioralDrift");
    }

    // ========================================================================
    // Loop-guard #2 (transience): the established centroid ABSORBS new
    // observations (FingerprintAbsorptionService.AbsorbAsync, maturity-weighted
    // mean), so a fingerprint that keeps presenting the SAME new stable shape
    // sees its centroid catch up -- current-vs-centroid similarity converges
    // back toward 1.0 and the drift contribution extinguishes. Drift is the
    // TRANSIENT alarm during the transition, not a permanent bot-ward force
    // that keeps pushing a fingerprint that "arrived" and stopped changing.
    // ========================================================================

    [Fact]
    public async Task RepeatedAbsorptionOfTheSameNewShape_ConvergesTheCentroid_AndExtinguishesDrift()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"fp-drift-transience-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new BotDetectionOptions
            {
                DatabasePath = Path.Combine(tempDir, "botdetection.db"),
                Identity = new IdentityOptions
                {
                    Enabled = true,
                    Vector = new IdentityVectorOptions
                    {
                        AbsorptionMaturityThreshold = 1,
                        AbsorptionAgeDays = 30,
                        ActiveWindowDays = 90,
                        // Pinned well past this test's runtime: FingerprintAbsorptionService
                        // subscribes to ObservationAppended at construction and schedules its
                        // OWN debounced background absorption (default 250ms) independent of
                        // this test's explicit TickOnceAsync polling below. On a slow CI runner
                        // the 30-iteration loop's cumulative wall-clock time can exceed 250ms,
                        // so the event-driven path starts firing mid-loop and races the explicit
                        // ticks -- double-absorbing an observation and producing a non-monotonic
                        // similarity reading (observed CI flake: "iteration 18: similarity must
                        // not regress"). A debounce far longer than the test can possibly run
                        // guarantees ONLY the explicit TickOnceAsync calls ever absorb.
                        SubscriptionDebounceMs = 300_000
                    }
                }
            });

            var layout = IdentityVectorLayout.DefaultV1();
            var store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, options, layout);
            await store.EnsureInitialisedAsync();
            var encoder = new IdentityVectorEncoder(layout);
            var archetypes = new IdentityArchetypeRegistry(NullLogger<IdentityArchetypeRegistry>.Instance, encoder);
            var service = new FingerprintAbsorptionService(
                NullLogger<FingerprintAbsorptionService>.Instance, store, archetypes, options);

            const string fpId = "fp-transience";
            var dim = layout.Dimension;
            var startingCentroid = new float[dim];
            startingCentroid[0] = 1.0f; // established shape: axis 0
            var weights = new float[dim];
            Array.Fill(weights, 1.0f);
            var now = DateTime.UtcNow;
            await store.InsertFingerprintAsync(new Fingerprint
            {
                FingerprintId = fpId,
                Centroid = startingCentroid,
                CentroidMaturity = 5, // established baseline, not cold-start
                Weights = weights,
                MemberCount = 1,
                ObservationCount = 5,
                CorrectionCount = 0,
                FirstSeen = now.AddDays(-7),
                LastSeen = now,
                Quality = 0.9,
                InferredClientType = "chrome-desktop",
                InferredTypeConfidence = 0.9,
                InferredTypeChangedAt = now.AddDays(-7)
            }, $"sig-{fpId}", CancellationToken.None);
            _ = await store.GetFingerprintAsync(fpId); // resident-load

            var newShape = new float[dim];
            newShape[1] = 1.0f; // the visitor's new, stable shape: axis 1 (orthogonal to axis 0)

            double SimilarityToNewShape(Fingerprint fp) =>
                BruteForceIdentityAnchorIndex.WeightedCosine(newShape, fp.Centroid, fp.Weights);

            var before = await store.GetFingerprintAsync(fpId);
            var similarityBefore = SimilarityToNewShape(before!);
            Assert.True(similarityBefore < 0.1, $"sanity: orthogonal shapes should start near-0 similarity, got {similarityBefore}");

            // No NEW contradicting evidence -- every observation presents the SAME new shape,
            // over and over. This must NOT be read as escalating divergence; each absorption
            // pulls the centroid toward it (maturity-weighted mean), converging similarity
            // upward rather than the drift alarm staying pinned or climbing.
            //
            // NOT asserted step-by-step: AbsorbAsync ALSO runs IdentityWeightMath.ApplyStability
            // (weight[i] nudged by how well THIS observation matched the PRE-absorption centroid)
            // + RenormaliseAndClamp every iteration. Every untouched dim (observation==centroid==0)
            // gets a stability boost too, so renormalisation redistributes total weight across many
            // dims each step -- a real, expected side effect that can make a SINGLE iteration's
            // weighted-cosine dip slightly even while the underlying centroid keeps converging.
            // The guardrail this test proves is the CHECKPOINT trend (does it keep climbing overall
            // and end up converged), not micro-monotonicity on every single absorption.
            var checkpoints = new List<double>();
            for (var i = 0; i < 30; i++)
            {
                await store.RecordObservationAsync(RequestScope.Unknown, fpId, newShape, ct: CancellationToken.None);
                await service.TickOnceAsync(CancellationToken.None);

                var current = await store.GetFingerprintAsync(fpId);
                checkpoints.Add(SimilarityToNewShape(current!));
            }

            var quarter = checkpoints.Count / 4;
            var q1 = checkpoints.Take(quarter).Average();
            var q4 = checkpoints.TakeLast(quarter).Average();
            Assert.True(q4 > q1,
                $"expected the later checkpoints to average higher than the earlier ones (no sustained backward trend), q1={q1}, q4={q4}");

            var lastSimilarity = checkpoints[^1];

            // Converged: the centroid has absorbed the new shape, so the SAME weighted-cosine
            // check IdentityChangeAtom runs now reads well above the warning threshold --
            // drift has extinguished, not stayed pinned or grown, from repeated exposure to
            // the same stable (no-new-information) shape alone.
            Assert.True(lastSimilarity > 0.92,
                $"expected convergence above the drift warning threshold, got {lastSimilarity}");

            // Cross-check through the real atom: no BehavioralDrift contribution fires once
            // the centroid has converged -- the score holds, drift does not keep pushing it.
            var converged = await store.GetFingerprintAsync(fpId);
            var atomContext = new DefaultHttpContext();
            atomContext.Items[IdentityVectorAtom.VectorKey] = newShape;
            var globalWeights = new IdentityGlobalWeightsCache(
                NullLogger<IdentityGlobalWeightsCache>.Instance, store, options);
            var atom = new IdentityChangeAtom(
                NullLogger<IdentityChangeAtom>.Instance,
                new StubDetectorConfigProvider(),
                store,
                new StaticHttpContextAccessor(atomContext),
                globalWeights);

            var sink = new SignalSink(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));
            sink.Raise($"{SignalKeys.IdentityFingerprintId}:{fpId}", Session);
            var result = await atom.DetectAsync(sink, Session);

            Assert.DoesNotContain(result, c => c.Category == "BehavioralDrift");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
