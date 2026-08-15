using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     The memory-only observation contract (write-path grain redesign, Phase B —
///     docs/architecture/write-path-grain-design.md §3.2/§7.5): the per-request
///     observation feed is MEMORY-ONLY — no durable row, no detail-row sampling. The
///     in-memory absorption fold (centroid EMA + maturity + weights) applies EVERY
///     observation on the request thread; the fingerprint's durable feed is the
///     fingerprint_mutations delta chain (needle-movers), not observation rows.
///     <para>
///     The adaptive-forgetting detail-row mechanism (novel vs confirmatory sampling)
///     retired with the feed: with no durable rows, the novelty gate had nothing to
///     bound. The knobs stay on the options classes for config back-compat; the
///     sampling decision itself no longer exists. Matcher quality is unchanged — the
///     centroid evolution math is identical, only the durable destination moved.
///     </para>
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
    public async Task RecordObservationAsync_is_memory_only_zero_rows_every_observation_folds()
    {
        // Phase B: no durable observation rows, ever — the DB never sees per-request
        // writes (the adaptive property). Both confirmatory AND novel observations fold
        // in memory on the request thread.
        var store = await NewStoreAsync();
        var dim = store.Layout.Dimension;
        const string fpId = "fp-confirmatory";
        var centroid = UnitVector(dim, 0);
        await SeedFingerprintAsync(store, fpId, centroid, maturity: 10);
        var before = await store.GetFingerprintAsync(fpId);

        // Same shape as the centroid (the old "confirmatory" case) AND an orthogonal
        // shape (the old "novel" case) — both are memory-only now.
        await store.RecordObservationAsync(RequestScope.Unknown, fpId, UnitVector(dim, 0));
        await store.RecordObservationAsync(RequestScope.Unknown, fpId, UnitVector(dim, 1));

        (await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None))
            .Should().Be(0, "the observation feed is memory-only — no detail rows exist");

        var after = await store.GetFingerprintAsync(fpId);
        after!.ObservationCount.Should().Be(before!.ObservationCount + 2,
            "every observation advances the in-memory observation count");
        after.CentroidMaturity.Should().Be(before.CentroidMaturity + 2,
            "the in-memory fold bumps maturity per observation — the fold is the sole owner on the request thread");

        // The latest observation vector is retained memory-first (the drift audit's
        // input — §7(3) memory-first verification).
        var latest = await store.GetLatestObservationVectorAsync(fpId, CancellationToken.None);
        latest.Should().NotBeNull();
        latest![1].Should().BeApproximately(1.0f, 1e-6f,
            "the latest observation (the orthogonal vector) is the retained one");
    }

    [Fact]
    public async Task RealDrift_StillRegisters_memory_first_latest_vector_served()
    {
        // feedback_foss_never_degraded, carried into the memory-only contract: a real
        // drift observation must still be VISIBLE to the drift readers. The fold-time
        // evaluator's positive control (SqliteFingerprintMutationTests) pins that a
        // known drift crossing emits a centroid_drift mutation; here we pin the input
        // side: the retained latest vector IS the drifted observation.
        var store = await NewStoreAsync(noveltyKeepThreshold: 0.05);
        var dim = store.Layout.Dimension;
        const string fpId = "fp-drift";
        await SeedFingerprintAsync(store, fpId, UnitVector(dim, 0), maturity: 10);

        // Normalised vector whose dot with the centroid is 0.9 => novelty 0.1.
        var drift = new float[dim];
        drift[0] = 0.9f;
        drift[1] = (float)Math.Sqrt(1.0 - 0.81); // ~0.4359, keeps unit norm
        await store.RecordObservationAsync(RequestScope.Unknown, fpId, drift);

        var latest = await store.GetLatestObservationVectorAsync(fpId, CancellationToken.None);
        latest.Should().NotBeNull();
        latest![0].Should().BeApproximately(0.9f, 1e-6f,
            "the drifted observation is the retained latest vector — the drift audit reads the live evolution");
        (await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None))
            .Should().Be(0, "still memory-only: no durable row, but nothing is forgotten by the readers");
    }

    [Fact]
    public async Task SamplingDisabled_ConfigKnob_Is_Retired_But_Parses()
    {
        // The adaptive-sampling knobs stay on the options classes for config
        // back-compat; with no detail rows the sampling decision no longer exists.
        // Legacy config that disabled sampling must still construct and behave the
        // same (memory-only).
        var store = await NewStoreAsync(adaptiveSampling: false);
        var dim = store.Layout.Dimension;
        const string fpId = "fp-legacy";
        await SeedFingerprintAsync(store, fpId, UnitVector(dim, 0), maturity: 10);

        await store.RecordObservationAsync(RequestScope.Unknown, fpId, UnitVector(dim, 0));

        (await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None))
            .Should().Be(0, "memory-only regardless of the retired sampling knob");
        var fp = await store.GetFingerprintAsync(fpId);
        fp!.ObservationCount.Should().Be(101, "the observation still folds in memory");
    }
}
