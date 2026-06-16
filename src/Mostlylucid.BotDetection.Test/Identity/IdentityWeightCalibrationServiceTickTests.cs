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
///     <see cref="IdentityWeightCalibrationService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     <c>Task.Delay(CalibrationIntervalMinutes)</c> loop; now subscribes to
///     <see cref="TickCadence.Tick1m"/> and gates the inner
///     <see cref="IdentityWeightCalibrationService.RunOnceAsync"/> pass on
///     "last-success older than configured CalibrationIntervalMinutes".
/// </summary>
public sealed class IdentityWeightCalibrationServiceTickTests
{
    private static IdentityWeightCalibrationService NewService(
        RecordingScheduleCoordinator coordinator,
        bool identityEnabled = true)
    {
        var store = new Mock<IFingerprintStore>(MockBehavior.Loose);
        store.SetupGet(s => s.Layout).Returns(IdentityVectorLayout.DefaultV1());
        store
            .Setup(s => s.ListFingerprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Fingerprint>)Array.Empty<Fingerprint>());

        var options = Options.Create(new BotDetectionOptions
        {
            Identity = new IdentityOptions { Enabled = identityEnabled }
        });
        var encoder = new IdentityVectorEncoder(IdentityVectorLayout.DefaultV1());
        var registry = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, encoder);

        return new IdentityWeightCalibrationService(
            NullLogger<IdentityWeightCalibrationService>.Instance,
            store.Object,
            registry,
            options,
            coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick1m_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1m);
        sub.Name.Should().Be("IdentityWeightCalibrationService");
        sub.Hint.Should().Be(CostHint.High);
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