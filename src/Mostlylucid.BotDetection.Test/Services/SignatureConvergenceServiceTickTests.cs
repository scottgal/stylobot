using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Wave 2 batch 2 regression coverage for
///     <see cref="SignatureConvergenceService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     <c>Task.Delay(EvaluationIntervalSeconds)</c> loop (load-sensor
///     adaptive); now subscribes to <see cref="TickCadence.Tick10s"/> and
///     gates the inner <c>RunEvaluation</c> pass on the load-sensor-stretched
///     interval since last run.
/// </summary>
public sealed class SignatureConvergenceServiceTickTests
{
    private static SignatureConvergenceService NewService(
        RecordingScheduleCoordinator coordinator,
        bool enabled = true)
    {
        var options = Options.Create(new BotDetectionOptions
        {
            SignatureConvergence =
            {
                Enabled = enabled,
                EvaluationIntervalSeconds = 15,
                MinSignaturesForMerge = 2,
                MergeScoreThreshold = 0.6,
                SplitDivergenceThreshold = 0.4,
                MinEvaluationsBeforeSplit = 3,
                MaxFamilies = 500,
                TemporalWeight = 0.3,
                BehavioralWeight = 0.3,
                BotProbabilityWeight = 0.4,
                TemporalProximityWindowSeconds = 300
            }
        });
        var signatureCoordinator = new SignatureCoordinator(
            NullLogger<SignatureCoordinator>.Instance, options);

        return new SignatureConvergenceService(
            NullLogger<SignatureConvergenceService>.Instance,
            options,
            signatureCoordinator,
            loadSensor: null,
            vectorSearch: null,
            scheduleCoordinator: coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick10s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick10s);
        sub.Name.Should().Be("SignatureConvergenceService");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_is_noop_when_convergence_disabled()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator, enabled: false);

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
