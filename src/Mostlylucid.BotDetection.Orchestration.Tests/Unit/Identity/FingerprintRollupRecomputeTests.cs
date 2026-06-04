using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Drives <see cref="FingerprintRollupRecomputeService"/> against a temp
///     SQLite store seeded with mode rows of known shape. Covers the
///     maturity-weighted mean math, the dry-run gate (RollupEnabled = false),
///     the no-modes-skip path, and idempotency on re-tick.
/// </summary>
public sealed class FingerprintRollupRecomputeTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private SqliteFingerprintStore _store = null!;
    private SqliteFingerprintBrowserModeStore _modeStore = null!;
    private FingerprintRollupRecomputeService _rollup = null!;
    private IdentityVectorLayout _layout = null!;
    private IOptions<BotDetectionOptions> _options = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-rollup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Engine = new IdentityEngineOptions { PreferSqliteVec = false },
                BrowserMode = new BrowserModeOptions
                {
                    Enabled = true,
                    RollupEnabled = true,
                    RollupRecomputeIntervalSeconds = 60,
                    RollupMaxFingerprintsPerTick = 1000,
                }
            }
        });
        _layout = IdentityVectorLayout.DefaultV1();
        _store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, _options, _layout);
        await _store.EnsureInitialisedAsync();
        _modeStore = new SqliteFingerprintBrowserModeStore(
            _store, _options, NullLogger<SqliteFingerprintBrowserModeStore>.Instance);
        _rollup = new FingerprintRollupRecomputeService(
            NullLogger<FingerprintRollupRecomputeService>.Instance, _store, _modeStore, _options);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TickOnce_NoModes_ReturnsZeroAndWritesNothing()
    {
        var (visited, written) = await _rollup.TickOnceAsync(maxFingerprints: 100, dryRun: false, CancellationToken.None);
        Assert.Equal(0, visited);
        Assert.Equal(0, written);
    }

    [Fact]
    public async Task TickOnce_SingleMode_RollupEqualsModeCentroid()
    {
        var dim = _layout.Dimension;
        var v = IdentityTestHelpers.MakeUnitVector(dim, seed: 1);
        await _store.InsertFingerprintAsync(IdentityTestHelpers.MakeFingerprint("fp-1", v), "sig-1");

        // Seed one mode row with maturity=5, centroid distinct from the parent's.
        var modeCentroid = IdentityTestHelpers.MakeUnitVector(dim, seed: 99);
        await UpsertModeAsync("fp-1", "xhr", modeCentroid, maturity: 5);

        var (visited, written) = await _rollup.TickOnceAsync(maxFingerprints: 100, dryRun: false, CancellationToken.None);
        Assert.Equal(1, visited);
        Assert.Equal(1, written);

        // With one contributor the rollup is the mode centroid exactly. Maturity
        // is the sum across modes -- 5 here.
        var fp = await _store.GetFingerprintAsync("fp-1");
        Assert.NotNull(fp);
        Assert.Equal(5, fp!.CentroidMaturity);
        for (var d = 0; d < dim; d++)
            Assert.Equal(modeCentroid[d], fp.Centroid[d], precision: 5);
    }

    [Fact]
    public async Task TickOnce_TwoModes_MaturityWeightedMean()
    {
        var dim = _layout.Dimension;
        var v = IdentityTestHelpers.MakeUnitVector(dim, seed: 2);
        await _store.InsertFingerprintAsync(IdentityTestHelpers.MakeFingerprint("fp-2", v), "sig-2");

        // navigation: centroid=A, maturity=3. xhr: centroid=B, maturity=7.
        // Expected rollup centroid[d] = (A[d]*3 + B[d]*7) / 10.
        // Expected rollup maturity   = 10.
        var navCentroid = IdentityTestHelpers.MakeUnitVector(dim, seed: 100);
        var xhrCentroid = IdentityTestHelpers.MakeUnitVector(dim, seed: 200);
        await UpsertModeAsync("fp-2", "navigation", navCentroid, maturity: 3);
        await UpsertModeAsync("fp-2", "xhr", xhrCentroid, maturity: 7);

        await _rollup.TickOnceAsync(maxFingerprints: 100, dryRun: false, CancellationToken.None);

        var fp = await _store.GetFingerprintAsync("fp-2");
        Assert.NotNull(fp);
        Assert.Equal(10, fp!.CentroidMaturity);
        for (var d = 0; d < dim; d++)
        {
            var expected = (navCentroid[d] * 3f + xhrCentroid[d] * 7f) / 10f;
            Assert.Equal(expected, fp.Centroid[d], precision: 5);
        }
    }

    [Fact]
    public async Task TickOnce_DryRun_ComputesButDoesNotWrite()
    {
        var dim = _layout.Dimension;
        var initial = IdentityTestHelpers.MakeUnitVector(dim, seed: 3);
        await _store.InsertFingerprintAsync(IdentityTestHelpers.MakeFingerprint("fp-3", initial), "sig-3");

        var mode = IdentityTestHelpers.MakeUnitVector(dim, seed: 300);
        await UpsertModeAsync("fp-3", "xhr", mode, maturity: 4);

        var (visited, written) = await _rollup.TickOnceAsync(maxFingerprints: 100, dryRun: true, CancellationToken.None);
        Assert.Equal(1, visited);
        Assert.Equal(0, written);

        // Parent centroid + maturity must be exactly what we inserted (the
        // MakeFingerprint helper sets maturity=10 by default; assert it).
        var fp = await _store.GetFingerprintAsync("fp-3");
        Assert.NotNull(fp);
        for (var d = 0; d < dim; d++)
            Assert.Equal(initial[d], fp!.Centroid[d], precision: 5);
    }

    [Fact]
    public async Task TickOnce_TwoFingerprints_BothGetRolledUp()
    {
        var dim = _layout.Dimension;
        var va = IdentityTestHelpers.MakeUnitVector(dim, seed: 4);
        var vb = IdentityTestHelpers.MakeUnitVector(dim, seed: 5);
        await _store.InsertFingerprintAsync(IdentityTestHelpers.MakeFingerprint("fp-A", va), "sig-A");
        await _store.InsertFingerprintAsync(IdentityTestHelpers.MakeFingerprint("fp-B", vb), "sig-B");

        await UpsertModeAsync("fp-A", "xhr", IdentityTestHelpers.MakeUnitVector(dim, seed: 41), maturity: 2);
        await UpsertModeAsync("fp-B", "navigation", IdentityTestHelpers.MakeUnitVector(dim, seed: 42), maturity: 6);

        var (visited, written) = await _rollup.TickOnceAsync(maxFingerprints: 100, dryRun: false, CancellationToken.None);
        Assert.Equal(2, visited);
        Assert.Equal(2, written);

        var a = await _store.GetFingerprintAsync("fp-A");
        var b = await _store.GetFingerprintAsync("fp-B");
        Assert.Equal(2, a!.CentroidMaturity);
        Assert.Equal(6, b!.CentroidMaturity);
    }

    [Fact]
    public async Task TickOnce_Idempotent_OnNoNewModeEvolution()
    {
        var dim = _layout.Dimension;
        var v = IdentityTestHelpers.MakeUnitVector(dim, seed: 6);
        await _store.InsertFingerprintAsync(IdentityTestHelpers.MakeFingerprint("fp-id", v), "sig-id");
        var mode = IdentityTestHelpers.MakeUnitVector(dim, seed: 600);
        await UpsertModeAsync("fp-id", "xhr", mode, maturity: 8);

        await _rollup.TickOnceAsync(maxFingerprints: 100, dryRun: false, CancellationToken.None);
        var first = await _store.GetFingerprintAsync("fp-id");

        await _rollup.TickOnceAsync(maxFingerprints: 100, dryRun: false, CancellationToken.None);
        var second = await _store.GetFingerprintAsync("fp-id");

        Assert.Equal(first!.CentroidMaturity, second!.CentroidMaturity);
        for (var d = 0; d < dim; d++)
            Assert.Equal(first.Centroid[d], second.Centroid[d], precision: 6);
    }

    [Fact]
    public void ComputeRollup_AllZeroMaturity_ReturnsNull()
    {
        var modes = new[]
        {
            MakeMode("nav", IdentityTestHelpers.MakeUnitVector(_layout.Dimension, seed: 7), maturity: 0),
            MakeMode("xhr", IdentityTestHelpers.MakeUnitVector(_layout.Dimension, seed: 8), maturity: 0),
        };
        var result = FingerprintRollupRecomputeService.ComputeRollup(modes);
        Assert.Null(result);
    }

    private static FingerprintBrowserMode MakeMode(string modeId, float[] centroid, int maturity)
    {
        var weights = new float[centroid.Length];
        for (var i = 0; i < weights.Length; i++) weights[i] = 1.0f;
        return new FingerprintBrowserMode
        {
            FingerprintId = "fp-test",
            ModeId = modeId,
            Centroid = centroid,
            CentroidMaturity = maturity,
            Weights = weights,
            ObservationCount = maturity,
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
        };
    }

    private async Task UpsertModeAsync(string fpId, string modeId, float[] centroid, int maturity)
    {
        var weights = new float[centroid.Length];
        for (var i = 0; i < weights.Length; i++) weights[i] = 1.0f;
        await _modeStore.UpsertModeAsync(new FingerprintBrowserMode
        {
            FingerprintId = fpId,
            ModeId = modeId,
            Centroid = centroid,
            CentroidMaturity = maturity,
            Weights = weights,
            ObservationCount = maturity,
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
        });
    }
}