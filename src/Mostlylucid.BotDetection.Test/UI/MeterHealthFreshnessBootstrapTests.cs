using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Mostlylucid.BotDetection.PrometheusPack.HealthSummaryProviders;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;
using Mostlylucid.Common.Scheduling;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Coverage for <see cref="MeterHealthFreshnessBootstrap"/> -- the Prometheus
///     pack's OWN meter-health freshness producer. This is the behaviour that
///     previously lived in the UI's <see cref="DashboardFreshnessBridge"/> meter
///     arm; it moved into the pack when Prometheus became an optional add-on so
///     the UI assembly carries no dependency on Prometheus types.
///     <para>
///         Contract (centralised change detection, feedback_centralised_change_detection):
///         the producer ticks on Tick10s, and invalidates the
///         <see cref="MeterStreamHealthTileCache"/> + broadcasts the
///         MeterStreamHealth surface ONLY when the catalog size changed.
///     </para>
/// </summary>
public sealed class MeterHealthFreshnessBootstrapTests
{
    // ---------- 1. Catalog size changed -> cache invalidated. -------------

    [Fact]
    public async Task Tick10s_with_changed_catalog_invalidates_tile_cache()
    {
        var stream = new FakeMeterStream();
        var coordinator = new FakeScheduleCoordinator();
        var tileCache = new MeterStreamHealthTileCache();
        tileCache.Set(new Mostlylucid.BotDetection.UI.Models.StatTileViewModel("Metrics", "0"));

        var bootstrap = new MeterHealthFreshnessBootstrap(
            beacon: NewBeacon(),
            stream: stream,
            cache: tileCache,
            coordinator: coordinator);

        await bootstrap.StartAsync(CancellationToken.None);

        // Initial tick: catalog size moves from the -1 sentinel to 0 ->
        // cache MUST invalidate. We assert the SYNCHRONOUS side effect (cache
        // invalidation), exactly like the original bridge tests -- the SignalR
        // broadcast flush is Task.Run-delayed and not deterministically
        // observable in-process.
        await coordinator.RaiseTickAsync(TickCadence.Tick10s);

        tileCache.TryGet().Should().BeNull(
            "the freshness bootstrap MUST invalidate the tile cache when the observed catalog size moves.");

        await bootstrap.StopAsync(CancellationToken.None);
    }

    // ---------- 2. No catalog change -> no re-invalidation. ----------------

    [Fact]
    public async Task Tick10s_without_catalog_change_does_not_reinvalidate()
    {
        var stream = new FakeMeterStream();
        var coordinator = new FakeScheduleCoordinator();
        var tileCache = new MeterStreamHealthTileCache();

        var bootstrap = new MeterHealthFreshnessBootstrap(
            beacon: NewBeacon(),
            stream: stream,
            cache: tileCache,
            coordinator: coordinator);

        await bootstrap.StartAsync(CancellationToken.None);

        // Tick #1: -1 -> 0 -> invalidate (already-null stays null).
        await coordinator.RaiseTickAsync(TickCadence.Tick10s);

        // Repopulate the cache; a consumer just rebuilt after the first beacon.
        tileCache.Set(new Mostlylucid.BotDetection.UI.Models.StatTileViewModel("Metrics", "0"));

        // Tick #2: catalog still empty (0 -> 0) -> MUST NOT re-invalidate.
        // Otherwise every tick would invalidate forever, defeating the
        // centralised-change-detection design.
        await coordinator.RaiseTickAsync(TickCadence.Tick10s);

        tileCache.TryGet().Should().NotBeNull(
            "an unchanged catalog must not invalidate the cache.");

        await bootstrap.StopAsync(CancellationToken.None);
    }

    // ---------- 3. No upstreams -> safe no-op. ----------------------------

    [Fact]
    public async Task Without_dashboard_beacon_or_coordinator_is_a_safe_noop()
    {
        // No beacon: a dashboard-less host (gateway-only Prometheus ingest).
        var coordinator = new FakeScheduleCoordinator();
        var bootstrapNoBeacon = new MeterHealthFreshnessBootstrap(
            beacon: null,
            stream: new FakeMeterStream(),
            coordinator: coordinator);

        var act = async () =>
        {
            await bootstrapNoBeacon.StartAsync(CancellationToken.None);
            await bootstrapNoBeacon.StopAsync(CancellationToken.None);
        };
        await act.Should().NotThrowAsync();

        // No stream / coordinator at all: fully optional-armed host.
        var empty = new MeterHealthFreshnessBootstrap();
        var act2 = async () =>
        {
            await empty.StartAsync(CancellationToken.None);
            await empty.StopAsync(CancellationToken.None);
        };
        await act2.Should().NotThrowAsync();
    }

    private static DashboardFreshnessBeacon NewBeacon()
    {
        var hub = new RecordingHub();
        var ctx = new RecordingHubContext(hub);
        return new DashboardFreshnessBeacon(ctx,
            new StyloBotDashboardOptions { BroadcastMinIntervalMs = 25 });
    }

    // ============================================================
    // Test doubles
    // ============================================================

    private sealed class FakeMeterStream : IMeterStream
    {
        public List<MeterCatalogEntry> Entries { get; } = new();

        public Task<IReadOnlyList<MeterCatalogEntry>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MeterCatalogEntry>>(Entries);

        public Task<MeterTimeSeries?> GetAsync(
            string meterName, TimeSpan window, int buckets, CancellationToken ct)
            => throw new NotSupportedException(
                "MeterHealthFreshnessBootstrap should only call ListAsync.");
    }

    private sealed class FakeScheduleCoordinator : IScheduleCoordinator
    {
        private readonly List<(TickCadence Cadence, Func<DateTimeOffset, CancellationToken, Task> Handler)> _subs = new();

        public IDisposable Subscribe(
            TickCadence cadence,
            string subscriberName,
            CostHint costHint,
            Func<DateTimeOffset, CancellationToken, Task> handler)
        {
            _subs.Add((cadence, handler));
            return new Subscription(() => _subs.RemoveAll(s => s.Handler == handler));
        }

        public IReadOnlyList<TickSubscriberMetadata> Snapshot()
            => Array.Empty<TickSubscriberMetadata>();

        public async Task RaiseTickAsync(TickCadence cadence)
        {
            foreach (var s in _subs.Where(x => x.Cadence == cadence).ToList())
            {
                await s.Handler(DateTimeOffset.UtcNow, CancellationToken.None);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Action _onDispose;
            public Subscription(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }
    }

    private sealed class RecordingHub : IStyloBotDashboardHub
    {
        public List<string> Signals { get; } = new();

        public Task BroadcastInvalidation(string signal)
        {
            lock (Signals) Signals.Add(signal);
            return Task.CompletedTask;
        }

        public Task BroadcastAttackArc(string countryCode, string riskBand) => Task.CompletedTask;
        public Task PolicyChanged(string scopeKey) => Task.CompletedTask;
        public Task FingerprintDirty(string fingerprintId, string slot) => Task.CompletedTask;
        public Task BroadcastDirty(DashboardDirtyBeacon beacon) => Task.CompletedTask;

        public int SignalCount(string signal)
        {
            lock (Signals) return Signals.Count(s => s == signal);
        }
    }

    private sealed class RecordingHubContext : IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>
    {
        private readonly RecordingClients _clients;

        public RecordingHubContext(RecordingHub hub)
        {
            _clients = new RecordingClients(hub);
        }

        public IHubClients<IStyloBotDashboardHub> Clients => _clients;
        public IGroupManager Groups => new NoopGroupManager();
    }

    private sealed class RecordingClients : IHubClients<IStyloBotDashboardHub>
    {
        private readonly IStyloBotDashboardHub _hub;
        public RecordingClients(IStyloBotDashboardHub hub) => _hub = hub;
        public IStyloBotDashboardHub All => _hub;
        public IStyloBotDashboardHub AllExcept(IReadOnlyList<string> excludedConnectionIds) => _hub;
        public IStyloBotDashboardHub Client(string connectionId) => _hub;
        public IStyloBotDashboardHub Clients(IReadOnlyList<string> connectionIds) => _hub;
        public IStyloBotDashboardHub Group(string groupName) => _hub;
        public IStyloBotDashboardHub GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _hub;
        public IStyloBotDashboardHub Groups(IReadOnlyList<string> groupNames) => _hub;
        public IStyloBotDashboardHub User(string userId) => _hub;
        public IStyloBotDashboardHub Users(IReadOnlyList<string> userIds) => _hub;
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
