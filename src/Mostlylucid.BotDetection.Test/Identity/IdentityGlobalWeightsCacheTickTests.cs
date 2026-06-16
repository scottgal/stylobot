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
///     Wave 2 batch 2 regression coverage for
///     <see cref="IdentityGlobalWeightsCache"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     <c>Task.Delay(GlobalRefreshSeconds)</c> loop; now subscribes to
///     <see cref="TickCadence.Tick10s"/> and gates the inner
///     <see cref="IdentityGlobalWeightsCache.RefreshAsync"/> pass on
///     "last-attempt older than configured GlobalRefreshSeconds".
/// </summary>
public sealed class IdentityGlobalWeightsCacheTickTests
{
    private static IdentityGlobalWeightsCache NewService(
        RecordingScheduleCoordinator coordinator,
        bool identityEnabled = true)
    {
        var store = new Mock<IFingerprintStore>(MockBehavior.Loose);
        store
            .Setup(s => s.GetGlobalWeightsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(((float[] Weights, DateTime LastComputedAt)?)null);

        var options = Options.Create(new BotDetectionOptions
        {
            Identity = new IdentityOptions { Enabled = identityEnabled }
        });

        return new IdentityGlobalWeightsCache(
            NullLogger<IdentityGlobalWeightsCache>.Instance,
            store.Object,
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
        sub.Name.Should().Be("IdentityGlobalWeightsCache");
        sub.Hint.Should().Be(CostHint.Low);
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