using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.Triggers;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     End-to-end integration of the Phase 1 adaptive trigger:
///     observations land in the store -> the shared
///     <see cref="CalibrationSignalSource"/> sees the increments -> the
///     calibration service's <see cref="IdentityWeightCalibrationService.OnTickAsync"/>
///     evaluates the policy and runs -> the signal counter resets.
///
///     <para>
///         Drives the loop without the schedule coordinator. We call
///         <see cref="IdentityWeightCalibrationService.OnTickAsync"/> directly
///         and the timestamp is supplied by the test, so the test never has
///         to sleep for a real heartbeat.
///     </para>
/// </summary>
public sealed class AdaptiveTriggerEndToEndTests : IDisposable
{
    private readonly string _tempDir;

    public AdaptiveTriggerEndToEndTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-adaptive-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Observation_pressure_drives_calibration_and_resets_the_counter()
    {
        // ---- Wire ---------------------------------------------------------
        var signals = new CalibrationSignalSource();
        var (store, _, calibration) = await BuildAsync(
            signals,
            triggerEnabled: true,
            obsThreshold: 5,
            minInterval: TimeSpan.FromMilliseconds(1));

        try
        {
            var dim = store.Layout.Dimension;
            await SeedFingerprintAsync(store, "fp-e2e", dim);

            // ---- Step 1: zero signal -> calibration must NOT fire ---------
            //
            // First-time call has LastSuccessfulRunUtc=null which trips the
            // safety net immediately (TimeSpan.MaxValue >= MaxInterval). We
            // want to assert the SIGNAL-driven path on a steady-state system,
            // so seed lastRunUtc via a primer call BEFORE feeding observations.
            var t0 = new DateTimeOffset(2026, 6, 21, 13, 0, 0, TimeSpan.Zero);
            await calibration.OnTickAsync(t0, CancellationToken.None);   // safety net fires the cold seed
            var snapshotAfterPrimer = calibration.LastDecisionSnapshot;
            Assert.True(snapshotAfterPrimer.ShouldRun,
                $"primer tick should fire via safety net, was '{snapshotAfterPrimer.Reason}'");

            // ---- Step 2: 3 observations < threshold(5) -> no fire ---------
            for (var i = 0; i < 3; i++)
                await store.RecordObservationAsync(RequestScope.Unknown, "fp-e2e", new float[dim]);

            // Heartbeat 1s after primer: min interval elapsed, but signal
            // (3 obs) is below threshold (5) and safety net hasn't elapsed.
            await calibration.OnTickAsync(t0.AddSeconds(1), CancellationToken.None);
            var underThresholdSnapshot = calibration.LastDecisionSnapshot;
            Assert.False(underThresholdSnapshot.ShouldRun,
                $"under-threshold heartbeat fired anyway: {underThresholdSnapshot.Reason}");
            Assert.Equal(3.0, underThresholdSnapshot.Signals[CalibrationSignalSource.SignalKeys.ObsUnabsorbed]);

            // ---- Step 3: 3 more observations -> 6 >= 5 -> fire ------------
            for (var i = 0; i < 3; i++)
                await store.RecordObservationAsync(RequestScope.Unknown, "fp-e2e", new float[dim]);

            await calibration.OnTickAsync(t0.AddSeconds(2), CancellationToken.None);
            var firedSnapshot = calibration.LastDecisionSnapshot;
            Assert.True(firedSnapshot.ShouldRun,
                $"over-threshold heartbeat did not fire: {firedSnapshot.Reason}");
            Assert.Contains("obs.unabsorbed", firedSnapshot.Reason);

            // ---- Step 4: counter MUST reset after a successful run --------
            //
            // The reset happens BEFORE RunOnceAsync per the spec so concurrent
            // obs during the run count toward the next cycle. Snapshot after
            // the fire MUST report 0 obs accumulated since the reset.
            var afterFire = signals.Snapshot();
            Assert.Equal(0.0, afterFire[CalibrationSignalSource.SignalKeys.ObsUnabsorbed]);
        }
        finally
        {
            calibration.Dispose();
        }
    }

    [Fact]
    public async Task Disabled_trigger_falls_back_to_legacy_interval_gate()
    {
        // Pin: when Trigger.Enabled = false, the service MUST honour the
        // legacy CalibrationIntervalMinutes gate. This is the production
        // safety net so existing deployments don't change behaviour.
        var signals = new CalibrationSignalSource();
        var (store, _, calibration) = await BuildAsync(
            signals,
            triggerEnabled: false,
            obsThreshold: 1,                    // would fire on any obs in adaptive mode
            minInterval: TimeSpan.FromSeconds(1));

        try
        {
            var dim = store.Layout.Dimension;
            await SeedFingerprintAsync(store, "fp-legacy", dim);

            // Legacy interval = 30 min default. With Trigger disabled, signals are ignored.
            // The primer call still fires (lastRun=MinValue forces it through the legacy
            // gate too) so we verify that the SECOND call within 30 min does NOT fire.
            var t0 = new DateTimeOffset(2026, 6, 21, 13, 0, 0, TimeSpan.Zero);
            await calibration.OnTickAsync(t0, CancellationToken.None);

            // Hammer the signal axis -- in adaptive mode this would trigger.
            for (var i = 0; i < 100; i++)
                await store.RecordObservationAsync(RequestScope.Unknown, "fp-legacy", new float[dim]);

            // Only 5 minutes later. Legacy gate must NOT fire (interval is 30m).
            await calibration.OnTickAsync(t0.AddMinutes(5), CancellationToken.None);

            // No decision snapshot is published when the trigger is disabled
            // (the service early-returns on the legacy interval check). The
            // pin is "signal counter still has the 100 obs" -- the legacy
            // path does not reset.
            var snap = signals.Snapshot();
            Assert.Equal(100.0, snap[CalibrationSignalSource.SignalKeys.ObsUnabsorbed]);
        }
        finally
        {
            calibration.Dispose();
        }
    }

    // ----- helpers -----------------------------------------------------------

    private async Task<(SqliteFingerprintStore Store, FingerprintAbsorptionService Absorption, IdentityWeightCalibrationService Calibration)>
        BuildAsync(
            CalibrationSignalSource signals,
            bool triggerEnabled,
            long obsThreshold,
            TimeSpan minInterval)
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, $"botdetection-{Guid.NewGuid():N}.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Vector = new IdentityVectorOptions
                {
                    AbsorptionMaturityThreshold = 1,
                    SubscriptionDebounceMs = 0,
                },
                Calibration = new IdentityCalibrationOptions
                {
                    CalibrationIntervalMinutes = 30,
                    Trigger = new CalibrationTriggerOptions
                    {
                        Enabled = triggerEnabled,
                        MinInterval = minInterval,
                        MaxInterval = TimeSpan.FromHours(6),
                        MaxBand = LoadBand.Critical,        // tests never have load pressure
                        ObservationThreshold = obsThreshold,
                        DriftL2Threshold = 0,               // obs-only for these tests
                    }
                }
            }
        });

        var layout = IdentityVectorLayout.DefaultV1();
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance, options, layout, signals);
        await store.EnsureInitialisedAsync();

        var encoder = new IdentityVectorEncoder(layout);
        var archetypes = new IdentityArchetypeRegistry(NullLogger<IdentityArchetypeRegistry>.Instance, encoder);

        var absorption = new FingerprintAbsorptionService(
            NullLogger<FingerprintAbsorptionService>.Instance,
            store,
            archetypes,
            options,
            scheduleCoordinator: null,
            triggerSignals: signals);

        var calibration = new IdentityWeightCalibrationService(
            NullLogger<IdentityWeightCalibrationService>.Instance,
            store,
            archetypes,
            options,
            scheduleCoordinator: null,
            triggerSignals: signals,
            loadBandSource: null);

        return (store, absorption, calibration);
    }

    private static async Task SeedFingerprintAsync(SqliteFingerprintStore store, string fpId, int dim)
    {
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        var now = DateTime.UtcNow;
        var fp = new Fingerprint
        {
            FingerprintId = fpId,
            Centroid = new float[dim],
            CentroidMaturity = 0,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 0,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "test",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
        };
        await store.InsertFingerprintAsync(fp, $"sig-{fpId}", CancellationToken.None);
    }
}
