using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private static Fingerprint Fixture(float[] centroid, int centroidMaturity) => new()
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
        InferredTypeChangedAt = DateTime.UtcNow.AddDays(-1)
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
}
