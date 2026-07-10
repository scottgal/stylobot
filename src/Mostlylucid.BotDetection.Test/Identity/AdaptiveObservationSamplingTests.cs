using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Adaptive forgetting on the identity write path: a confirmatory observation on an
///     already-matured fingerprint is SUMMARISED (count + maturity advance, no detail row),
///     while novel observations and observations on still-maturing fingerprints keep a full
///     detail row. This bounds fingerprint_observations by behavioural novelty rather than
///     request volume, so a high-cardinality look-alike flood cannot balloon the identity store.
///
///     Critical invariant (FOSS never loses detection sensitivity): a real drift observation
///     (novelty at or above the keep threshold) is NEVER sampled away.
/// </summary>
public sealed class AdaptiveObservationSamplingTests : IDisposable
{
    private readonly string _tempDir;

    public AdaptiveObservationSamplingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-adaptive-obs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<SqliteFingerprintStore> NewStoreAsync(
        bool adaptiveSampling = true,
        double noveltyKeepThreshold = 0.05,
        int maturityThreshold = 5)
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, $"botdetection-{Guid.NewGuid():N}.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Vector = new IdentityVectorOptions
                {
                    AbsorptionMaturityThreshold = maturityThreshold,
                    AdaptiveObservationSampling = adaptiveSampling,
                    ObservationNoveltyKeepThreshold = noveltyKeepThreshold,
                    AbsorptionAgeDays = 30,
                    ActiveWindowDays = 90
                }
            }
        });
        var layout = IdentityVectorLayout.DefaultV1();
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance, options, layout);
        await store.EnsureInitialisedAsync();
        return store;
    }

    /// <summary>Seed a fingerprint with a chosen (L2-normalised) centroid and maturity.</summary>
    private static async Task SeedFingerprintAsync(
        SqliteFingerprintStore store, string fpId, float[] centroid, int maturity)
    {
        var dim = store.Layout.Dimension;
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        var fp = new Fingerprint
        {
            FingerprintId = fpId,
            Centroid = centroid,
            CentroidMaturity = maturity,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 100,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "chrome-desktop",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now
        };
        await store.InsertFingerprintAsync(fp, $"sig-{fpId}", CancellationToken.None);
    }

    /// <summary>A unit vector along one axis (L2-normalised, so Cosine collapses to the dot).</summary>
    private static float[] UnitVector(int dim, int axis)
    {
        var v = new float[dim];
        v[axis] = 1.0f;
        return v;
    }

    [Fact]
    public async Task ConfirmatoryObservation_OnMaturedFingerprint_IsSummarisedNotPersisted()
    {
        var store = await NewStoreAsync();
        var dim = store.Layout.Dimension;
        const string fpId = "fp-confirmatory";
        var centroid = UnitVector(dim, 0);
        await SeedFingerprintAsync(store, fpId, centroid, maturity: 10);
        var before = await store.GetFingerprintAsync(fpId);

        // Same shape as the centroid: novelty 0, well below the 0.05 keep threshold.
        await store.RecordObservationAsync(RequestScope.Unknown, fpId, UnitVector(dim, 0));

        (await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None))
            .Should().Be(0, "a confirmatory observation must not write a detail row");
        store.SummarisedObservationCount.Should().Be(1, "it was summarised instead");

        var after = await store.GetFingerprintAsync(fpId);
        after!.ObservationCount.Should().Be(before!.ObservationCount + 1,
            "the summarised entry still advances the observation count");
        after.CentroidMaturity.Should().Be(before.CentroidMaturity + 1,
            "maturity advances so the fold accounting stays honest and the centroid keeps stabilising");
    }

    [Fact]
    public async Task NovelObservation_OnMaturedFingerprint_KeepsDetailRow()
    {
        var store = await NewStoreAsync();
        var dim = store.Layout.Dimension;
        const string fpId = "fp-novel";
        await SeedFingerprintAsync(store, fpId, UnitVector(dim, 0), maturity: 10);

        // Orthogonal shape: novelty 1.0, far above the keep threshold.
        await store.RecordObservationAsync(RequestScope.Unknown, fpId, UnitVector(dim, 1));

        (await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None))
            .Should().Be(1, "a novel observation must keep a full detail row");
        store.SummarisedObservationCount.Should().Be(0);
    }

    [Fact]
    public async Task ImmatureFingerprint_AlwaysKeepsDetail_EvenWhenConfirmatory()
    {
        // Below the maturity threshold the fingerprint is still building its identity,
        // so every observation earns detail regardless of novelty.
        var store = await NewStoreAsync(maturityThreshold: 5);
        var dim = store.Layout.Dimension;
        const string fpId = "fp-immature";
        await SeedFingerprintAsync(store, fpId, UnitVector(dim, 0), maturity: 2);

        await store.RecordObservationAsync(RequestScope.Unknown, fpId, UnitVector(dim, 0));

        (await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None))
            .Should().Be(1, "an immature (identity-building) fingerprint keeps every observation");
        store.SummarisedObservationCount.Should().Be(0);
    }

    [Fact]
    public async Task RealDrift_JustAboveKeepThreshold_IsNeverSampledAway()
    {
        // feedback_foss_never_degraded: a 10% shape change (novelty 0.1 > threshold 0.05)
        // is genuine drift and MUST persist detail so the drift reader can see it.
        var store = await NewStoreAsync(noveltyKeepThreshold: 0.05);
        var dim = store.Layout.Dimension;
        const string fpId = "fp-drift";
        await SeedFingerprintAsync(store, fpId, UnitVector(dim, 0), maturity: 10);

        // Normalised vector whose dot with the centroid is 0.9 => novelty 0.1.
        var drift = new float[dim];
        drift[0] = 0.9f;
        drift[1] = (float)Math.Sqrt(1.0 - 0.81); // ~0.4359, keeps unit norm
        await store.RecordObservationAsync(RequestScope.Unknown, fpId, drift);

        (await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None))
            .Should().Be(1, "real drift above the keep threshold is never forgotten");
        store.SummarisedObservationCount.Should().Be(0);
    }

    [Fact]
    public async Task SamplingDisabled_PersistsEveryObservation()
    {
        var store = await NewStoreAsync(adaptiveSampling: false);
        var dim = store.Layout.Dimension;
        const string fpId = "fp-legacy";
        await SeedFingerprintAsync(store, fpId, UnitVector(dim, 0), maturity: 10);

        // Even a perfectly confirmatory observation persists when sampling is off.
        await store.RecordObservationAsync(RequestScope.Unknown, fpId, UnitVector(dim, 0));

        (await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None))
            .Should().Be(1, "legacy behaviour: every observation keeps a detail row");
        store.SummarisedObservationCount.Should().Be(0);
    }
}
