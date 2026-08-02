using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Wave 2 batch 1 regression coverage for
///     <see cref="FingerprintDriftService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     <c>Task.Delay(DriftCheckIntervalSeconds)</c> loop; now subscribes to
///     <see cref="TickCadence.Tick10s"/> and gates the inner
///     <c>TickOnceAsync</c> pass on "last-success older than configured
///     <see cref="IdentityDriftOptions.DriftCheckIntervalSeconds"/>".
/// </summary>
public sealed class FingerprintDriftServiceTickTests
{
    private static FingerprintDriftService NewService(
        RecordingScheduleCoordinator coordinator,
        bool identityEnabled = true)
    {
        var fpStore = new Mock<IFingerprintStore>(MockBehavior.Loose);
        // ListStaleScoreFingerprintsAsync returning empty -> TickOnceAsync
        // returns (0, 0) without further DB / weight work.
        fpStore
            .Setup(s => s.ListStaleScoreFingerprintsAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Fingerprint>)Array.Empty<Fingerprint>());

        var options = Options.Create(new BotDetectionOptions
        {
            Identity = new IdentityOptions { Enabled = identityEnabled }
        });
        var globalWeights = new IdentityGlobalWeightsCache(
            NullLogger<IdentityGlobalWeightsCache>.Instance, fpStore.Object, options);
        var identityCoordinator = new IdentityProcessingCoordinator(
            NullLogger<IdentityProcessingCoordinator>.Instance, options);

        return new FingerprintDriftService(
            NullLogger<FingerprintDriftService>.Instance,
            fpStore.Object,
            globalWeights,
            identityCoordinator,
            options,
            coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick10s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick10s);
        sub.Name.Should().Be("FingerprintDriftService");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_is_noop_when_identity_disabled()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator, identityEnabled: false);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
    }

    private static Fingerprint DriftFixture(string id) => new()
    {
        FingerprintId = id,
        // Centroid points along dim 0; the "latest observation" set up in each test
        // points along dim 1 -- orthogonal, so WeightedCosine returns exactly 0,
        // deterministically below any DriftWarningThreshold.
        Centroid = new float[] { 1f, 0f, 0f, 0f },
        CentroidMaturity = 50,
        Weights = new float[] { 1f, 1f, 1f, 1f },
        MemberCount = 1,
        ObservationCount = 50,
        CorrectionCount = 0,
        FirstSeen = DateTime.UtcNow.AddDays(-1),
        LastSeen = DateTime.UtcNow,
        Quality = 0.9,
        InferredClientType = "human-adblocker",
        InferredTypeConfidence = 0.9,
        InferredTypeChangedAt = DateTime.UtcNow.AddDays(-1),
        CachedBotProbability = 0.05, // clean history -- the exact prod shape (Adblocker -> curl)
    };

    [Fact]
    public async Task TickOnceAsync_opens_the_drift_reopen_window_when_drift_is_detected()
    {
        // Phase 1 of the 2026-08-02 fp-cache-current architecture mandate: drift detection
        // must not be a dead-end log line. When weighted-cosine drift crosses the warning
        // threshold, the service must open the fast-absorption window so the NEXT verdict
        // writes converge quickly instead of staying stuck on the stale cached score.
        var coordinator = new RecordingScheduleCoordinator();
        var fp = DriftFixture("drifted-fp");

        var fpStore = new Mock<IFingerprintStore>(MockBehavior.Loose);
        fpStore
            .Setup(s => s.ListStaleScoreFingerprintsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Fingerprint>)new[] { fp });
        fpStore
            .Setup(s => s.GetLatestObservationVectorAsync(fp.FingerprintId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 0f, 1f, 0f, 0f }); // orthogonal to Centroid -> cosine 0

        var options = Options.Create(new BotDetectionOptions
        {
            Identity = new IdentityOptions { Enabled = true }
        });
        var globalWeights = new IdentityGlobalWeightsCache(
            NullLogger<IdentityGlobalWeightsCache>.Instance, fpStore.Object, options);
        var identityCoordinator = new IdentityProcessingCoordinator(
            NullLogger<IdentityProcessingCoordinator>.Instance, options);

        var sut = new FingerprintDriftService(
            NullLogger<FingerprintDriftService>.Instance,
            fpStore.Object,
            globalWeights,
            identityCoordinator,
            options,
            coordinator);

        var (checkedCount, drifts) = await sut.TickOnceAsync(CancellationToken.None);

        checkedCount.Should().Be(1);
        drifts.Should().Be(1);
        fpStore.Verify(s => s.MarkDriftReopenedAsync(
                fp.FingerprintId,
                It.Is<DateTime>(d => d > DateTime.UtcNow),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "drift detection must open the fast-absorption window, not just log a warning");
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();

        var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose is safe.
        sut.Dispose();
    }
}