using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity.BrowserModes;

/// <summary>
///     Adaptive forgetting on the browser-mode write path: a confirmatory observation on an
///     already-matured mode is summarised (mode count + maturity advance, no detail row) while
///     novel observations and observations on still-maturing modes keep a full detail row. This
///     bounds fingerprint_mode_observations by novelty rather than volume — the mode-observation
///     table was the largest under a high-cardinality flood.
/// </summary>
public sealed class AdaptiveModeObservationSamplingTests : IDisposable
{
    private readonly string _tempDir;

    public AdaptiveModeObservationSamplingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-adaptive-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<(SqliteFingerprintStore Store, SqliteFingerprintBrowserModeStore ModeStore, int Dim)>
        NewStoresAsync(bool adaptiveSampling = true, double noveltyKeepThreshold = 0.05, int maturityThreshold = 5)
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
                },
                BrowserMode = new BrowserModeOptions { Enabled = true }
            }
        });
        var layout = IdentityVectorLayout.DefaultV1();
        var store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, options, layout);
        await store.EnsureInitialisedAsync();
        var modeStore = new SqliteFingerprintBrowserModeStore(
            store, options, NullLogger<SqliteFingerprintBrowserModeStore>.Instance);
        return (store, modeStore, layout.Dimension);
    }

    /// <summary>fingerprint_modes has a FK to fingerprints, so the parent row must exist first.</summary>
    private static async Task SeedParentFingerprintAsync(SqliteFingerprintStore store, string fpId, int dim)
    {
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        await store.InsertFingerprintAsync(new Fingerprint
        {
            FingerprintId = fpId,
            Centroid = new float[dim],
            CentroidMaturity = 1,
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
        }, $"sig-{fpId}", CancellationToken.None);
    }

    private static async Task SeedModeAsync(
        SqliteFingerprintBrowserModeStore modeStore, string fpId, string modeId, float[] centroid, int maturity)
    {
        var dim = centroid.Length;
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        await modeStore.UpsertModeAsync(new FingerprintBrowserMode
        {
            FingerprintId = fpId,
            ModeId = modeId,
            Centroid = centroid,
            CentroidMaturity = maturity,
            Weights = weights,
            ObservationCount = 100,
            FirstSeen = now,
            LastSeen = now
        }, CancellationToken.None);
    }

    private static float[] UnitVector(int dim, int axis)
    {
        var v = new float[dim];
        v[axis] = 1.0f;
        return v;
    }

    private static async Task<int> UnabsorbedCountAsync(
        SqliteFingerprintBrowserModeStore modeStore, string fpId, string modeId)
    {
        var rows = await modeStore.ListUnabsorbedModeObservationsAsync(10_000, CancellationToken.None);
        return rows.Count(r => r.FingerprintId == fpId && r.ModeId == modeId);
    }

    [Fact]
    public async Task ConfirmatoryModeObservation_OnMaturedMode_IsSummarisedNotPersisted()
    {
        var (store, modeStore, dim) = await NewStoresAsync();
        const string fpId = "fp-mode-confirmatory";
        const string modeId = "navigation";
        await SeedParentFingerprintAsync(store, fpId, dim);
        await SeedModeAsync(modeStore, fpId, modeId, UnitVector(dim, 0), maturity: 10);

        await modeStore.RecordModeObservationAsync(RequestScope.Unknown, fpId, modeId, UnitVector(dim, 0));

        (await UnabsorbedCountAsync(modeStore, fpId, modeId))
            .Should().Be(0, "a confirmatory mode observation must not write a detail row");
        modeStore.SummarisedModeObservationCount.Should().Be(1);

        var after = await modeStore.GetModeAsync(fpId, modeId, CancellationToken.None);
        after!.CentroidMaturity.Should().Be(10,
            "summarise must NOT touch mode maturity: the drainer owns the fold; a second writer desyncs it");
        after.ObservationCount.Should().Be(101);
    }

    [Fact]
    public async Task NovelModeObservation_OnMaturedMode_KeepsDetailRow()
    {
        var (store, modeStore, dim) = await NewStoresAsync();
        const string fpId = "fp-mode-novel";
        const string modeId = "navigation";
        await SeedParentFingerprintAsync(store, fpId, dim);
        await SeedModeAsync(modeStore, fpId, modeId, UnitVector(dim, 0), maturity: 10);

        await modeStore.RecordModeObservationAsync(RequestScope.Unknown, fpId, modeId, UnitVector(dim, 1));

        (await UnabsorbedCountAsync(modeStore, fpId, modeId))
            .Should().Be(1, "a novel mode observation must keep a full detail row");
        modeStore.SummarisedModeObservationCount.Should().Be(0);
    }

    [Fact]
    public async Task ImmatureMode_AlwaysKeepsDetail()
    {
        var (store, modeStore, dim) = await NewStoresAsync(maturityThreshold: 5);
        const string fpId = "fp-mode-immature";
        const string modeId = "navigation";
        await SeedParentFingerprintAsync(store, fpId, dim);
        await SeedModeAsync(modeStore, fpId, modeId, UnitVector(dim, 0), maturity: 2);

        await modeStore.RecordModeObservationAsync(RequestScope.Unknown, fpId, modeId, UnitVector(dim, 0));

        (await UnabsorbedCountAsync(modeStore, fpId, modeId))
            .Should().Be(1, "a still-maturing mode keeps every observation");
        modeStore.SummarisedModeObservationCount.Should().Be(0);
    }

    [Fact]
    public async Task NewMode_AlwaysKeepsDetail()
    {
        // No fingerprint_modes row yet: the observation is the mode's first, so keep detail.
        var (store, modeStore, dim) = await NewStoresAsync();
        const string fpId = "fp-mode-new";
        const string modeId = "navigation";
        await SeedParentFingerprintAsync(store, fpId, dim);

        await modeStore.RecordModeObservationAsync(RequestScope.Unknown, fpId, modeId, UnitVector(dim, 0));

        (await UnabsorbedCountAsync(modeStore, fpId, modeId))
            .Should().Be(1, "the first observation of an unseen mode always persists");
        modeStore.SummarisedModeObservationCount.Should().Be(0);
    }
}
