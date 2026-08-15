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
///     The memory-only mode-observation contract (write-path grain redesign, Phase B —
///     docs/architecture/write-path-grain-design.md §3.2/§7.5): the per-request mode
///     observation feed is MEMORY-ONLY — no durable row, ever. Mode RESOLUTION continues
///     in the matcher; the mode centroid's durable evolution ends with the observation
///     feed (its role was the mode absorption's DB input, which now finds no rows and
///     no-ops). Mode TRANSITIONS become fold-time mutations at the sweep
///     (fingerprint_mutations.mode_transition). Extra traffic folds in memory — the DB
///     never sees per-request writes.
///     <para>
///     The adaptive-forgetting detail-row mechanism retired with the feed; the knobs stay
///     on the options classes for config back-compat.
///     </para>
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

    [Fact]
    public async Task RecordModeObservationAsync_is_memory_only_zero_rows_always()
    {
        // Phase B: no durable mode-observation rows, ever — for confirmatory, novel,
        // immature and unseen-mode observations alike. The mode feed's DB role ends.
        var (store, modeStore, dim) = await NewStoresAsync();
        const string fpId = "fp-mode-memory-only";
        const string modeId = "navigation";
        await SeedParentFingerprintAsync(store, fpId, dim);
        await SeedModeAsync(modeStore, fpId, modeId, UnitVector(dim, 0), maturity: 10);

        await modeStore.RecordModeObservationAsync(RequestScope.Unknown, fpId, modeId, UnitVector(dim, 0));
        await modeStore.RecordModeObservationAsync(RequestScope.Unknown, fpId, modeId, UnitVector(dim, 1));

        var rows = await modeStore.ListUnabsorbedModeObservationsAsync(10_000, CancellationToken.None);
        rows.Should().BeEmpty("the mode observation feed is memory-only — no durable rows exist");

        // Mode state itself is untouched by the observation (the mode centroid's durable
        // evolution ended with the feed; mode resolution continues in the matcher).
        var after = await modeStore.GetModeAsync(fpId, modeId, CancellationToken.None);
        after!.CentroidMaturity.Should().Be(10, "mode observations no longer evolve the durable mode centroid");
        after.ObservationCount.Should().Be(100);
    }

    [Fact]
    public async Task UnseenMode_Observation_Writes_Nothing()
    {
        // Even a mode with no fingerprint_modes row yet: the observation is memory-only.
        var (store, modeStore, dim) = await NewStoresAsync();
        const string fpId = "fp-mode-new";
        const string modeId = "navigation";
        await SeedParentFingerprintAsync(store, fpId, dim);

        await modeStore.RecordModeObservationAsync(RequestScope.Unknown, fpId, modeId, UnitVector(dim, 0));

        var rows = await modeStore.ListUnabsorbedModeObservationsAsync(10_000, CancellationToken.None);
        rows.Should().BeEmpty("the first observation of an unseen mode is memory-only like every other");
    }
}
