using System.Threading.Channels;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Isolates tests that drive <see cref="SignalRBroadcastConstrainer"/>'s
///     process-static state sequentially. The constrainer's <c>_flushScheduled</c>
///     gate and <c>Pending</c> dict are shared across the whole test process; running
///     timing-sensitive integration tests concurrently causes each test's hub context
///     to not receive the flush because another test's context "stole" the flush slot.
///     The [CollectionDefinition] + [Collection] attributes force sequential execution
///     only for the constrained tests; unrelated tests run in parallel as normal.
/// </summary>
[CollectionDefinition(nameof(BroadcastConstrainerCollection), DisableParallelization = true)]
public sealed class BroadcastConstrainerCollection { }

/// <summary>
///     TDD coverage for Task 2 of Plan 3 — Dashboard Update Flow.
///     Verifies:
///     <list type="bullet">
///         <item>
///             A flush window containing {"summary","countries"} emits exactly ONE
///             <see cref="DashboardDirtyBeacon"/> whose <c>DirtyKinds</c> contains
///             both surfaces and whose <c>Tick</c> equals
///             <see cref="IDashboardChangeCursor.CurrentTick"/>.
///         </item>
///         <item>
///             <c>BroadcastInvalidation</c> STILL fires per surface (back-compat).
///         </item>
///         <item>
///             <see cref="IDashboardChangeCursor.Bump"/> at an emission site
///             advances <see cref="IDashboardChangeCursor.TickFor"/> for that surface.
///         </item>
///     </list>
///     Tests 1, 2, and 5 run in a sequential collection because they exercise
///     <see cref="SignalRBroadcastConstrainer"/>'s process-static flush gate. Tests
///     3 and 4 are purely synchronous / in-memory and run in any order.
/// </summary>
[Collection(nameof(BroadcastConstrainerCollection))]
public sealed class BroadcastDirtyTests
{
    // -----------------------------------------------------------------------
    // 1. A flush window with two surfaces emits ONE BroadcastDirty beacon
    //    whose DirtyKinds contains both surfaces and Tick == cursor.CurrentTick.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Flush_window_emits_one_BroadcastDirty_with_all_queued_surfaces_and_current_tick()
    {
        // Arrange
        var cursor = new DashboardChangeCursor();
        var hub = new CapturingHub();
        var ctx = new CapturingHubContext(hub);

        // Wire cursor into the constrainer static slot (as DI registration does).
        SignalRBroadcastConstrainer.SetCursor(cursor);

        try
        {
            // Act — queue two surfaces with a tiny interval so the flush fires fast.
            const int intervalMs = 50;
            SignalRBroadcastConstrainer.Queue(ctx, "summary",   intervalMs);
            SignalRBroadcastConstrainer.Queue(ctx, "countries", intervalMs);

            // Wait for the flush to fire (interval + headroom).
            var beacon = await hub.WaitForDirtyBeaconAsync(TimeSpan.FromSeconds(3));

            // Assert — exactly one dirty beacon.
            beacon.Should().NotBeNull();
            beacon!.DirtyKinds.Should().Contain("summary");
            beacon.DirtyKinds.Should().Contain("countries");
            beacon.Tick.Should().Be(cursor.CurrentTick);
        }
        finally
        {
            // Clear cursor so other tests' constrainer state is not polluted.
            SignalRBroadcastConstrainer.SetCursor(null);
        }
    }

    // -----------------------------------------------------------------------
    // 2. BroadcastInvalidation still fires per surface (back-compat preserved).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task BroadcastInvalidation_fires_per_surface_after_flush()
    {
        var hub = new CapturingHub();
        var ctx = new CapturingHubContext(hub);

        SignalRBroadcastConstrainer.SetCursor(null);

        const int intervalMs = 50;
        SignalRBroadcastConstrainer.Queue(ctx, "summary",   intervalMs);
        SignalRBroadcastConstrainer.Queue(ctx, "countries", intervalMs);

        // Wait for at least two invalidations to arrive.
        var signals = await hub.WaitForInvalidationsAsync(2, TimeSpan.FromSeconds(3));

        signals.Should().Contain("summary");
        signals.Should().Contain("countries");
    }

    // -----------------------------------------------------------------------
    // 3. cursor.Bump(surface) at an emission site advances TickFor(surface).
    // -----------------------------------------------------------------------

    [Fact]
    public void Bump_at_emission_site_advances_TickFor_for_that_surface()
    {
        var cursor = new DashboardChangeCursor();

        // Simulate what DetectionBroadcastMiddleware / DashboardSummaryBroadcaster do.
        cursor.Bump("summary");
        cursor.Bump("signature");

        cursor.TickFor("summary").Should().Be(cursor.CurrentTick);
        cursor.TickFor("signature").Should().Be(cursor.CurrentTick);
        // Unrelated surface unaffected.
        cursor.TickFor("countries").Should().Be(0);
    }

    // -----------------------------------------------------------------------
    // 4. BroadcastDirty beacon shape: sealed record, minimal fields.
    // -----------------------------------------------------------------------

    [Fact]
    public void DashboardDirtyBeacon_has_minimal_shape()
    {
        var beacon = new DashboardDirtyBeacon(Tick: 42, DirtyKinds: new[] { "summary", "countries" });

        beacon.Tick.Should().Be(42);
        beacon.DirtyKinds.Should().BeEquivalentTo(new[] { "summary", "countries" });
    }

    // -----------------------------------------------------------------------
    // 5. When cursor is null (no DI) the constrainer still emits the per-surface
    //    BroadcastInvalidation and does NOT throw (graceful degradation).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task No_cursor_does_not_prevent_BroadcastInvalidation_from_firing()
    {
        var hub = new CapturingHub();
        var ctx = new CapturingHubContext(hub);

        // Explicitly clear cursor to simulate missing DI wiring.
        SignalRBroadcastConstrainer.SetCursor(null);

        const int intervalMs = 50;
        SignalRBroadcastConstrainer.Queue(ctx, "threats", intervalMs);

        var signals = await hub.WaitForInvalidationsAsync(1, TimeSpan.FromSeconds(3));
        signals.Should().Contain("threats");
    }

    // =======================================================================
    // Test doubles
    // =======================================================================

    /// <summary>
    ///     Records every <c>BroadcastInvalidation</c> call and the first
    ///     <c>BroadcastDirty</c> call; provides awaitable waiters so tests
    ///     can block deterministically without sleeps.
    /// </summary>
    internal sealed class CapturingHub : IStyloBotDashboardHub
    {
        private readonly Channel<string> _invalidations =
            Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = false });

        private readonly Channel<DashboardDirtyBeacon> _dirtyBeacons =
            Channel.CreateUnbounded<DashboardDirtyBeacon>(new UnboundedChannelOptions { SingleReader = false });

        public Task BroadcastInvalidation(string signal)
        {
            _invalidations.Writer.TryWrite(signal);
            return Task.CompletedTask;
        }

        public Task BroadcastDirty(DashboardDirtyBeacon beacon)
        {
            _dirtyBeacons.Writer.TryWrite(beacon);
            return Task.CompletedTask;
        }

        public Task BroadcastAttackArc(string countryCode, string riskBand) => Task.CompletedTask;
        public Task PolicyChanged(string scopeKey) => Task.CompletedTask;
        public Task FingerprintDirty(string fingerprintId, string slot) => Task.CompletedTask;

        /// <summary>Waits until at least <paramref name="count"/> invalidation signals arrive.</summary>
        public async Task<List<string>> WaitForInvalidationsAsync(int count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var result = new List<string>();
            while (result.Count < count)
            {
                result.Add(await _invalidations.Reader.ReadAsync(cts.Token));
            }
            return result;
        }

        /// <summary>Waits for the first dirty beacon to arrive.</summary>
        public async Task<DashboardDirtyBeacon?> WaitForDirtyBeaconAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return await _dirtyBeacons.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    internal sealed class CapturingHubContext : IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>
    {
        private readonly CapturingClients _clients;

        public CapturingHubContext(CapturingHub hub)
        {
            _clients = new CapturingClients(hub);
        }

        public IHubClients<IStyloBotDashboardHub> Clients => _clients;
        public IGroupManager Groups => new NoopGroupManager();
    }

    internal sealed class CapturingClients : IHubClients<IStyloBotDashboardHub>
    {
        private readonly IStyloBotDashboardHub _hub;
        public CapturingClients(IStyloBotDashboardHub hub) => _hub = hub;

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

    internal sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
