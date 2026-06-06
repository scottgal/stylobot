using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Task 4 of the Identity Async Un-Drift plan: pins the contract that
///     <see cref="FingerprintAbsorptionService"/> absorbs on
///     <see cref="IFingerprintStore.ObservationAppended"/> with per-fp debounce,
///     and that the backstop loop uses <see cref="IdentityVectorOptions.BackstopSweepIntervalSeconds"/>
///     rather than the old Drift.DriftCheckIntervalSeconds.
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
        int backstopSweepSeconds = 300,
        int debounceMs = 250)
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
                    BackstopSweepIntervalSeconds = backstopSweepSeconds,
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
            options);

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
        var (store, service) = await BuildAsync(backstopSweepSeconds: 300, debounceMs: 200);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StartAsync(cts.Token);
        try
        {
            var fpId = await SeedFingerprintAsync(store, "fp-absorb-1");

            // Record one observation; this fires ObservationAppended.
            await store.RecordObservationAsync(fpId, new float[store.Layout.Dimension], CancellationToken.None);

            // Wait debounce + 300ms buffer.
            await Task.Delay(500, CancellationToken.None);

            // If absorption ran, the observation should now be absorbed (count = 0).
            var pending = await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None);
            Assert.Equal(0, pending);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RapidObservations_CollapseToSingleAbsorptionWithinDebounce()
    {
        // Debounce = 200ms. 10 rapid observations for the same fp must coalesce to one absorption run.
        var (store, service) = await BuildAsync(backstopSweepSeconds: 300, debounceMs: 200);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StartAsync(cts.Token);
        try
        {
            var fpId = await SeedFingerprintAsync(store, "fp-burst-1");
            var dim = store.Layout.Dimension;

            // Record 10 observations in quick succession.
            for (var i = 0; i < 10; i++)
                await store.RecordObservationAsync(fpId, new float[dim], CancellationToken.None);

            // Wait debounce + 400ms buffer.
            await Task.Delay(600, CancellationToken.None);

            // Absorption should have run; counter should show all observations absorbed.
            var pending = await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None);
            Assert.Equal(0, pending);

            // The per-fp absorption counter must be exactly 1, not 10.
            // This is the key contract: debounce collapses the burst.
            Assert.Equal(1, service.EventDrivenAbsorptionCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task BackstopSweep_RunsAtConfiguredCadence_NotAtSubSecondFloor()
    {
        // backstopSweepSeconds = 1; run for 2.5s; expect 1-4 backstop ticks (not hundreds).
        var (store, service) = await BuildAsync(backstopSweepSeconds: 1, debounceMs: 50);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await service.StartAsync(cts.Token);
        try
        {
            await Task.Delay(2500, CancellationToken.None);

            // 1 second cadence over 2.5s: expect 1, 2, or at most 4 (allowing for timing jitter).
            // The old 5s DriftCheckIntervalSeconds-driven path was 1s-floored, which would never
            // give 500 ticks; but the math here is: with 1s cadence and 2.5s window we expect 2.
            // We assert > 0 (ran at least once) and < 10 (did not tick every few ms).
            Assert.InRange(service.BackstopSweepCount, 1, 9);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
