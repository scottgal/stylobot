using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Task 4 of the Identity Async Un-Drift plan: pins the contract that
///     <see cref="FingerprintAbsorptionService"/> absorbs on
///     <see cref="IFingerprintStore.ObservationAppended"/> with per-fp debounce.
///     <para>
///         Wave 2 Cat-C* update: the backstop loop is now driven by
///         <see cref="Mostlylucid.Common.Scheduling.IScheduleCoordinator"/>'s
///         Tick5m subscription rather than a self-managed
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> + Task.Delay
///         loop. The event-driven fast-path is wired at construction (no StartAsync
///         needed) and remains the primary absorption mechanism; the tick is the
///         catch-up that the rollup service expects to see consistent state on.
///     </para>
/// </summary>
public class FingerprintAbsorptionServiceSubscribeTests : IDisposable
{
    private readonly string _tempDir;

    public FingerprintAbsorptionServiceSubscribeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-absorb-sub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<(SqliteFingerprintStore Store, FingerprintAbsorptionService Service)> BuildAsync(
        int debounceMs = 250,
        RecordingScheduleCoordinator? coordinator = null)
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, $"botdetection-{Guid.NewGuid():N}.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Vector = new IdentityVectorOptions
                {
                    AbsorptionMaturityThreshold = 1,   // absorb after 1 observation so tests fire fast
                    AbsorptionAgeDays = 30,
                    ActiveWindowDays = 90,
                    SubscriptionDebounceMs = debounceMs
                }
            }
        });

        var layout = IdentityVectorLayout.DefaultV1();
        var store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, options, layout);
        await store.EnsureInitialisedAsync();

        var encoder = new IdentityVectorEncoder(layout);
        var archetypes = new IdentityArchetypeRegistry(NullLogger<IdentityArchetypeRegistry>.Instance, encoder);

        var service = new FingerprintAbsorptionService(
            NullLogger<FingerprintAbsorptionService>.Instance,
            store,
            archetypes,
            options,
            coordinator);

        return (store, service);
    }

    private static async Task<string> SeedFingerprintAsync(SqliteFingerprintStore store, string fpId)
    {
        var dim = store.Layout.Dimension;
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        var fp = new Fingerprint
        {
            FingerprintId = fpId,
            Centroid = new float[dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "chrome-desktop",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now
        };
        await store.InsertFingerprintAsync(fp, $"sig-{fpId}", CancellationToken.None);
        return fpId;
    }

    [Fact]
    public async Task ObservationAppended_TriggersAbsorptionWithinDebounce()
    {
        // debounce = 200ms, so absorption should fire within 200ms + buffer.
        var (store, service) = await BuildAsync(debounceMs: 200);
        try
        {
            var fpId = await SeedFingerprintAsync(store, "fp-absorb-1");

            // Record one observation; this fires ObservationAppended.
            await store.RecordObservationAsync(RequestScope.Unknown, fpId, new float[store.Layout.Dimension], ct: CancellationToken.None);

            // Poll until absorbed rather than sleeping a fixed duration.
            // The debounce fires after ~200ms then the Task.Run worker must be
            // scheduled; under heavy parallel test load that can take well over
            // the old 300ms buffer. 10 s is the hard ceiling.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            int pending;
            do
            {
                await Task.Delay(50, CancellationToken.None);
                pending = await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None);
            }
            while (pending > 0 && DateTime.UtcNow < deadline);

            Assert.Equal(0, pending);
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task RapidObservations_CollapseToSingleAbsorptionWithinDebounce()
    {
        // Debounce = 200ms. 10 rapid observations for the same fp must coalesce to one absorption run.
        var (store, service) = await BuildAsync(debounceMs: 200);
        try
        {
            var fpId = await SeedFingerprintAsync(store, "fp-burst-1");
            var dim = store.Layout.Dimension;

            // Record 10 observations in quick succession.
            for (var i = 0; i < 10; i++)
                await store.RecordObservationAsync(RequestScope.Unknown, fpId, new float[dim], ct: CancellationToken.None);

            // Poll until absorbed rather than a fixed sleep: the debounced run fires
            // ~200ms after the last observation, then the drainer must persist all 10
            // writes. Under heavy parallel CI load that exceeds a fixed 600ms budget
            // (the poll-not-sleep deflake pattern, mirroring
            // ObservationAppended_TriggersAbsorptionWithinDebounce). 10 s hard ceiling.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            int pending;
            do
            {
                await Task.Delay(50, CancellationToken.None);
                pending = await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None);
            }
            while (pending > 0 && DateTime.UtcNow < deadline);

            Assert.Equal(0, pending);

            // The per-fp absorption counter must be exactly 1, not 10.
            // This is the key contract: debounce collapses the burst (unaffected by
            // how long the async persist takes, so polling does not weaken it).
            Assert.Equal(1, service.EventDrivenAbsorptionCount);
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task BackstopTick_RunsBackstopSweep()
    {
        // Wave 2 Cat-C*: the backstop sweep now fires on the schedule
        // coordinator's tick; tests drive the captured handler directly under a
        // RecordingScheduleCoordinator. Three ticks => three backstop sweeps.
        var coord = new RecordingScheduleCoordinator();
        var (_, service) = await BuildAsync(debounceMs: 200, coordinator: coord);
        try
        {
            var sub = Assert.Single(coord.Subscriptions);
            await sub.Handler(DateTimeOffset.UtcNow, CancellationToken.None);
            await sub.Handler(DateTimeOffset.UtcNow, CancellationToken.None);
            await sub.Handler(DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(3, service.BackstopSweepCount);
        }
        finally
        {
            service.Dispose();
        }
    }
}