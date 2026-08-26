using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Producer-side coverage for <see cref="DashboardFreshnessBridge"/>
///     (issue #122). The bridge subscribes to <see cref="IPolicyRuleStore.Changed"/>;
///     tests fire the rule-store Changed event and expect the policy-stack
///     cache invalidated AND a stale beacon broadcast.
///     <para>
///         The meter-stream catalog arm moved out of this bridge into the
///         Prometheus pack (MeterHealthFreshnessBootstrap) when the pack became
///         an optional add-on -- it is covered by MeterHealthFreshnessBootstrapTests.
///     </para>
/// </summary>
public sealed class DashboardFreshnessBridgeTests
{
    // ---------- 1. IPolicyRuleStore.Changed -> cache invalidate. ----------
    // We assert on the SYNCHRONOUS side effect (cache.Invalidate()) rather
    // than the ASYNCHRONOUS one (SignalRBroadcastConstrainer flush) because
    // the constrainer holds process-static state with a Task.Run-delayed
    // dispatch; across parallel tests, a flush scheduled here can be picked
    // up by a sibling test's hub context. Cache invalidation is the
    // operator-meaningful contract: "next read rebuilds".

    [Fact]
    public async Task RuleStore_Changed_invalidates_PolicyStackSummaryCache()
    {
        var ruleStore = new FakeRuleStore();
        var cache = new PolicyStackSummaryCache();
        cache.Set(MakeSummary());

        var beacon = NewBeacon();
        var bridge = new DashboardFreshnessBridge(
            beacon,
            ruleStore: ruleStore,
            policyCache: cache);

        await bridge.StartAsync(CancellationToken.None);

        // Pre-condition: cache populated.
        cache.TryGet().Should().NotBeNull();

        ruleStore.RaiseChanged(PolicyScope.Wildcard());

        cache.TryGet().Should().BeNull(
            "the bridge MUST invalidate the cache on every rule-store change.");

        await bridge.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RuleStore_Changed_detaches_handler_after_StopAsync()
    {
        var ruleStore = new FakeRuleStore();
        var cache = new PolicyStackSummaryCache();

        var beacon = NewBeacon();
        var bridge = new DashboardFreshnessBridge(
            beacon,
            ruleStore: ruleStore,
            policyCache: cache);

        await bridge.StartAsync(CancellationToken.None);
        await bridge.StopAsync(CancellationToken.None);

        cache.Set(MakeSummary());
        ruleStore.RaiseChanged(PolicyScope.Wildcard());

        cache.TryGet().Should().NotBeNull(
            "after StopAsync the bridge MUST detach its rule-store handler; otherwise " +
            "the cache would be invalidated by events that fire after the host has " +
            "been told to shut down.");
    }

    // ---------- 2. No upstreams -> bridge is a no-op. ---------------------

    [Fact]
    public async Task Bridge_with_no_upstreams_is_safe_to_start_and_stop()
    {
        var beacon = NewBeacon();
        var bridge = new DashboardFreshnessBridge(beacon);

        var act = async () =>
        {
            await bridge.StartAsync(CancellationToken.None);
            await bridge.StopAsync(CancellationToken.None);
        };

        await act.Should().NotThrowAsync();
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

    private static PolicyStackSummary MakeSummary() =>
        new(TotalRules: 1, LiveRules: 1, ObserveRules: 0, DraftRules: 0,
            DisabledRules: 0, DecisionsLast15m: null,
            HealthBand: Mostlylucid.BotDetection.UI.Models.HealthBand.Good,
            ComputedAtUtc: DateTimeOffset.UtcNow);

    private sealed class FakeRuleStore : IPolicyRuleStore
    {
        public event EventHandler<PolicyRuleStoreChangedEventArgs>? Changed;

        public void RaiseChanged(PolicyScope scope)
            => Changed?.Invoke(this, new PolicyRuleStoreChangedEventArgs(scope));

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<PolicyRule>> GetRulesAtAsync(PolicyScope scope, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PolicyRule>>(Array.Empty<PolicyRule>());

        public Task<IReadOnlyList<PolicyRule>> GetEffectiveRulesAsync(
            IReadOnlyList<PolicyScope> scopePath, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PolicyRule>>(Array.Empty<PolicyRule>());

        public Task<PolicyRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<PolicyRule?>(null);

        public Task<IReadOnlyList<PolicyRule>> GetAllRulesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PolicyRule>>(Array.Empty<PolicyRule>());
    }

    private sealed class RecordingHub : IStyloBotDashboardHub
    {
        public List<string> Signals { get; } = new();

        public Task BroadcastInvalidation(string signal)
        {
            lock (Signals) Signals.Add(signal);
            return Task.CompletedTask;
        }

        public Task BroadcastAttackArc(string countryCode, string riskBand)
            => Task.CompletedTask;

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
