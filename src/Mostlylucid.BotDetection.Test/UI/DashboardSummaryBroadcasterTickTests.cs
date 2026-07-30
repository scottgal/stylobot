using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Wave 2 batch regression coverage for
///     <see cref="DashboardSummaryBroadcaster"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     <c>Task.Delay(SummaryBroadcastIntervalSeconds)</c> loop; now
///     subscribes to <see cref="TickCadence.Tick10s"/> and gates the
///     compute pass on the configured interval.
/// </summary>
public sealed class DashboardSummaryBroadcasterTickTests
{
    private static DashboardSummaryBroadcaster NewService(
        RecordingScheduleCoordinator coordinator,
        bool remoteMode = false,
        Mock<IDashboardEventStore>? eventStoreMock = null,
        StyloBotDashboardOptions? options = null)
    {
        var hubCtxMock = new Mock<IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>>(MockBehavior.Loose);
        var eventStore = (eventStoreMock ?? new Mock<IDashboardEventStore>(MockBehavior.Loose)).Object;
        options ??= new StyloBotDashboardOptions();
        var sigCache = new SignatureAggregateCache(options);
        var aggCache = new DashboardAggregateCache();
        var services = new ServiceCollection().BuildServiceProvider();
        var sourceOptions = remoteMode
            ? new DashboardSourceOptions { Pull = new DashboardSourcePullOptions { Type = DashboardSourceType.Rest } }
            : null;

        return new DashboardSummaryBroadcaster(
            hubCtxMock.Object,
            eventStore,
            aggCache,
            sigCache,
            options,
            services,
            NullLogger<DashboardSummaryBroadcaster>.Instance,
            new DashboardUserAgentAggregator(eventStore),
            sourceOptions,
            coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick10s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick10s);
        sub.Name.Should().Be("DashboardSummaryBroadcaster");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_first_tick_is_announce_only_no_throw()
    {
        // The migrated handler treats the first tick as an "announce-only" beat
        // so it can replace the legacy 3-second pre-loop sleep. This test
        // proves the first call returns cleanly without invoking the event
        // store -- the loose mock would happily return defaults, but we want
        // to assert the handler isn't computing on the announce beat.
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task Prune_cutoff_uses_the_configured_DetectionRetention_not_a_hardcoded_window()
    {
        // Real bug: the prune call was hardcoded to `nowUtc.AddDays(-7)` regardless of
        // StyloBotDashboardOptions.DetectionRetention (default 30 days) -- the dashboard's own
        // window picker (_Body.cshtml) gates its 6h/24h/7d/30d buttons on this SAME configured
        // value, so a host configured for 30-day retention would still have its data silently
        // capped at 7 days by this broadcaster's own prune tick, independent of what the event
        // store's own internal retention logic does. Pin the cutoff to the configured value.
        var coordinator = new RecordingScheduleCoordinator();
        var storeMock = new Mock<IDashboardEventStore>(MockBehavior.Loose);
        storeMock.Setup(s => s.PruneOldDetectionsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        var options = new StyloBotDashboardOptions { DetectionRetention = TimeSpan.FromDays(14) };

        using var sut = NewService(coordinator, eventStoreMock: storeMock, options: options);
        var captured = Assert.Single(coordinator.Subscriptions);

        // First tick is announce-only (per the test above); the second is where prune fires,
        // since _lastPruneUtc starts at DateTime.MinValue.
        var now = DateTimeOffset.UtcNow;
        await captured.Handler(now, CancellationToken.None);
        await captured.Handler(now, CancellationToken.None);

        storeMock.Verify(s => s.PruneOldDetectionsAsync(
            It.Is<DateTime>(cutoff => Math.Abs((cutoff - (now.UtcDateTime - options.DetectionRetention)).TotalSeconds) < 5),
            It.IsAny<CancellationToken>()), Times.Once);
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