using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Drives <see cref="FingerprintDriftService"/> directly via its public TickOnceAsync entry
///     point against a real SQLite store in a temp file. The store is the verifier here: the
///     service writes <c>cached_score_updated_at</c> on every checked fingerprint, and the
///     drift counter reflects how many fell below the warning threshold.
/// </summary>
public sealed class FingerprintDriftServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BotDetectionOptions _options;
    private readonly SqliteFingerprintStore _store;
    private readonly FingerprintDriftService _service;

    public FingerprintDriftServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-drift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _options = new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Drift = new IdentityDriftOptions
                {
                    CachedScoreTtlSeconds = 1,
                    DriftBatchSize = 50,
                    DriftWarningThreshold = 0.92
                }
            }
        };
        var optionsWrapper = Options.Create(_options);
        var layout = IdentityVectorLayout.DefaultV1();
        _store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, optionsWrapper, layout);
        var globalWeights = new IdentityGlobalWeightsCache(
            NullLogger<IdentityGlobalWeightsCache>.Instance, _store, optionsWrapper);
        var coordinator = new IdentityProcessingCoordinator(
            NullLogger<IdentityProcessingCoordinator>.Instance, optionsWrapper);
        coordinator.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _service = new FingerprintDriftService(
            NullLogger<FingerprintDriftService>.Instance, _store, globalWeights, coordinator, optionsWrapper,
            NullScheduleCoordinator.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task TickOnceAsync_NoFingerprints_ReturnsZero()
    {
        await _store.EnsureInitialisedAsync();

        var (checks, drifts) = await _service.TickOnceAsync(CancellationToken.None);

        Assert.Equal(0, checks);
        Assert.Equal(0, drifts);
    }

    [Fact]
    public async Task TickOnceAsync_StableFingerprint_ChecksWithoutDrift()
    {
        // Centroid and observation are identical → cosine = 1.0, well above 0.92 threshold.
        var dim = _store.Layout.Dimension;
        var vec = IdentityTestHelpers.MakeUnitVector(dim, seed: 1);
        await _store.InsertFingerprintAsync(MakeFingerprint("stable-fp", vec), "sig-stable");
        await _store.RecordObservationAsync("stable-fp", vec);

        var (checks, drifts) = await _service.TickOnceAsync(CancellationToken.None);

        Assert.Equal(1, checks);
        Assert.Equal(0, drifts);
        var refreshed = await _store.GetFingerprintAsync("stable-fp");
        Assert.NotNull(refreshed!.CachedScoreUpdatedAt);
    }

    [Fact]
    public async Task TickOnceAsync_ObservationOrthogonalToCentroid_FlagsDrift()
    {
        // Centroid and observation are orthogonal → cosine = 0, far below 0.92.
        var dim = _store.Layout.Dimension;
        var centroid = IdentityTestHelpers.MakeUnitVector(dim, seed: 1);
        var orthogonalObs = IdentityTestHelpers.MakeUnitVectorOrthogonalTo(dim, seed: 2, orthogonalTo: centroid);
        await _store.InsertFingerprintAsync(MakeFingerprint("drifting-fp", centroid), "sig-drift");
        await _store.RecordObservationAsync("drifting-fp", orthogonalObs);

        var (checks, drifts) = await _service.TickOnceAsync(CancellationToken.None);

        Assert.Equal(1, checks);
        Assert.Equal(1, drifts);
        var refreshed = await _store.GetFingerprintAsync("drifting-fp");
        Assert.NotNull(refreshed!.CachedScoreUpdatedAt);
    }

    [Fact]
    public async Task TickOnceAsync_FreshFingerprint_SkippedUntilTtlExpires()
    {
        var dim = _store.Layout.Dimension;
        var vec = IdentityTestHelpers.MakeUnitVector(dim, seed: 3);
        await _store.InsertFingerprintAsync(MakeFingerprint("fresh-fp", vec), "sig-fresh");
        await _store.RecordObservationAsync("fresh-fp", vec);

        // First tick claims it (cached_score_updated_at was null).
        var (checks1, _) = await _service.TickOnceAsync(CancellationToken.None);
        Assert.Equal(1, checks1);

        // Immediate second tick: TTL is 1s, cached_score_updated_at is fresh → skipped.
        var (checks2, _) = await _service.TickOnceAsync(CancellationToken.None);
        Assert.Equal(0, checks2);

        // After TTL expires it picks the fingerprint up again.
        await Task.Delay(1100);
        var (checks3, _) = await _service.TickOnceAsync(CancellationToken.None);
        Assert.Equal(1, checks3);
    }

    [Fact]
    public async Task TickOnceAsync_FingerprintWithNoObservations_NotPicked()
    {
        var dim = _store.Layout.Dimension;
        var vec = IdentityTestHelpers.MakeUnitVector(dim, seed: 4);
        await _store.InsertFingerprintAsync(MakeFingerprint("orphan-fp", vec), "sig-orphan");
        // No RecordObservationAsync call — observation_count stays 0; drift service skips it.

        var (checks, _) = await _service.TickOnceAsync(CancellationToken.None);
        Assert.Equal(0, checks);
    }

    // Drift-service fixtures use observationCount=0 because the test arranges
    // observations explicitly via RecordObservationAsync after insert.
    private static Fingerprint MakeFingerprint(string id, float[] centroid) =>
        IdentityTestHelpers.MakeFingerprint(
            id, centroid, inferredClientType: "test-client", observationCount: 0);
}
