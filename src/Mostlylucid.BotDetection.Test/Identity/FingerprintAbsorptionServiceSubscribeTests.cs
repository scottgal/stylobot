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
        RecordingScheduleCoordinator? coordinator = null,
        IdentityProcessingCoordinator? slowPathCoordinator = null)
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
            coordinator,
            slowPathCoordinator: slowPathCoordinator);

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
    public async Task ObservationAppended_FiresAndTheMemoryFoldAppliesOnTheRequestThread()
    {
        // Phase B (write-path grain redesign): the observation feed is MEMORY-ONLY —
        // the fold happens on the request thread inside RecordObservationAsync; the
        // absorption service finds no rows and no-ops. The event still fires; the
        // fingerprint's evolution is immediately visible (no DB round-trip to wait for).
        var (store, service) = await BuildAsync(debounceMs: 200);
        try
        {
            var fpId = await SeedFingerprintAsync(store, "fp-absorb-1");

            await store.RecordObservationAsync(RequestScope.Unknown, fpId, new float[store.Layout.Dimension], ct: CancellationToken.None);

            // Zero rows exist (memory-only) — the absorb pipeline's input is gone.
            Assert.Equal(0, await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None));

            // The in-memory fold already applied: observation count advanced.
            var fp = await store.GetFingerprintAsync(fpId, CancellationToken.None);
            Assert.True(fp!.ObservationCount >= 2, "the memory fold advanced the observation count");
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task ObservationAppended_EventFiresRegardlessOfAbsorptionPipeline()
    {
        // The event contract survives the Phase B fold: ObservationAppended fires on
        // every observation even though the absorption service's DB role ended (the
        // subscribers — the calibration trigger, the dashboard — still ride the event).
        var (store, service) = await BuildAsync(debounceMs: 200);
        try
        {
            var fpId = await SeedFingerprintAsync(store, "fp-seq-1");

            int fired = 0;
            store.ObservationAppended += _ => Interlocked.Increment(ref fired);

            await store.RecordObservationAsync(RequestScope.Unknown, fpId, new float[store.Layout.Dimension], ct: CancellationToken.None);
            await store.RecordObservationAsync(RequestScope.Unknown, fpId, new float[store.Layout.Dimension], ct: CancellationToken.None);

            Assert.Equal(2, fired);
            Assert.Equal(0, await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None));
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task ObservationBurst_FoldsEveryObservationInMemory_NoDBWrites()
    {
        // Phase B: the DB never sees per-request writes (the adaptive property). A 10-
        // observation burst folds all 10 into the LFU; zero rows land; the absorption
        // service's debounce counters stay at their no-op baseline.
        var (store, service) = await BuildAsync(debounceMs: 200);
        try
        {
            var fpId = await SeedFingerprintAsync(store, "fp-burst-1");
            var dim = store.Layout.Dimension;
            var before = await store.GetFingerprintAsync(fpId, CancellationToken.None);

            for (var i = 0; i < 10; i++)
                await store.RecordObservationAsync(RequestScope.Unknown, fpId, new float[dim], ct: CancellationToken.None);

            Assert.Equal(0, await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None));

            var after = await store.GetFingerprintAsync(fpId, CancellationToken.None);
            Assert.Equal(before!.ObservationCount + 10, after!.ObservationCount);
            Assert.Equal(before.CentroidMaturity + 10, after.CentroidMaturity);
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