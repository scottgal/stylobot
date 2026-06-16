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