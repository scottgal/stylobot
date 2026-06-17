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
    public async Task First_RunOnceAsync_cold_seeds_every_archetype_into_the_store()
    {
        // Without this, only archetypes that accumulate descendant fingerprints
        // get upserted via the calibration path -- so a fresh DB stays missing
        // every root for which no observation has landed yet. That's what
        // produced the StyloBot.Internal / Mastodon / GPTBot gaps after the
        // staging wipe; the in-memory registry had those roots, the DB did not.
        var store = new Mock<IFingerprintStore>(MockBehavior.Loose);
        store.SetupGet(s => s.Layout).Returns(IdentityVectorLayout.DefaultV1());
        store
            .Setup(s => s.ListFingerprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Fingerprint>)Array.Empty<Fingerprint>());

        var upserted = new List<string>();
        store
            .Setup(s => s.UpsertArchetypeAsync(It.IsAny<IdentityArchetype>(), It.IsAny<CancellationToken>()))
            .Callback<IdentityArchetype, CancellationToken>((a, _) => upserted.Add(a.ArchetypeId))
            .Returns(Task.CompletedTask);

        var options = Options.Create(new BotDetectionOptions
        {
            Identity = new IdentityOptions { Enabled = true }
        });
        var encoder = new IdentityVectorEncoder(IdentityVectorLayout.DefaultV1());
        var registry = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, encoder);

        var sut = new IdentityWeightCalibrationService(
            NullLogger<IdentityWeightCalibrationService>.Instance,
            store.Object,
            registry,
            options,
            scheduleCoordinator: null); // bypass scheduling -- drive RunOnceAsync directly

        await sut.RunOnceAsync(CancellationToken.None);

        upserted.Count.Should().Be(registry.All.Count,
            "the first RunOnceAsync call must upsert every registered root so the durable " +
            "identity_archetypes table holds the full taxonomy before any traffic-driven " +
            "refinement begins");
        upserted.Should().Contain("chrome-desktop");
        upserted.Should().Contain("ublock-origin");

        // Second pass must NOT re-cold-seed (the flag is one-shot per process).
        upserted.Clear();
        await sut.RunOnceAsync(CancellationToken.None);
        upserted.Should().BeEmpty(
            "subsequent calibration passes only upsert archetypes that accumulated descendants " +
            "since the previous run; with no fingerprints in the store, none should be touched");
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