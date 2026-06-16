using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Markov;

/// <summary>
///     Wave 2 batch 2 regression coverage for
///     <see cref="PopulationMarkovService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     <c>Task.Delay(CohortFlushIntervalSeconds)</c> loop; now subscribes to
///     <see cref="TickCadence.Tick10s"/> and flushes cohort updates every
///     tick while gating the snapshot log line on
///     <see cref="MarkovOptions.SnapshotIntervalSeconds"/>.
/// </summary>
public sealed class PopulationMarkovServiceTickTests
{
    private static PopulationMarkovService NewService(
        RecordingScheduleCoordinator coordinator,
        MarkovTracker? tracker = null)
    {
        tracker ??= new MarkovTracker(
            NullLogger<MarkovTracker>.Instance,
            Options.Create(new BotDetectionOptions()));

        return new PopulationMarkovService(
            NullLogger<PopulationMarkovService>.Instance,
            tracker,
            Options.Create(new BotDetectionOptions()),
            coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick10s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick10s);
        sub.Name.Should().Be("PopulationMarkovService");
        sub.Hint.Should().Be(CostHint.Low);
    }

    [Fact]
    public async Task OnTickAsync_flushes_cohort_updates_without_throwing()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

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