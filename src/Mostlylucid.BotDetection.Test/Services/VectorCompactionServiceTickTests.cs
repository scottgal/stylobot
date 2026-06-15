using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Wave 2 batch 1 regression coverage for
///     <see cref="VectorCompactionService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> that
///     slept until the configured CompactionHourUtc; now subscribes to
///     <see cref="TickCadence.Tick1h"/> and runs only when the current UTC
///     hour matches the configured hour AND we haven't already run today.
/// </summary>
public sealed class VectorCompactionServiceTickTests
{
    private sealed class NoopSignatureStore : ISignatureCentroidStore
    {
        public Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SignatureCentroidRow>>(Array.Empty<SignatureCentroidRow>());
    }

    private sealed class NoopSessionStore : ISessionCentroidStore
    {
        public Task PruneSessionsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionCentroidRow>>(Array.Empty<SessionCentroidRow>());
    }

    private sealed class NoopIntentStore : IIntentCentroidStore
    {
        public Task PruneIntentsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertIntentAsync(string signatureId, float[] vector, double threatScore, string intentCategory, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<IntentCentroidRow>> GetRecentIntentsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IntentCentroidRow>>(Array.Empty<IntentCentroidRow>());
    }

    private static VectorCompactionService NewService(
        RecordingScheduleCoordinator coordinator)
    {
        var sessionStore = new Mock<ISessionStore>(MockBehavior.Loose);
        sessionStore
            .Setup(s => s.GetOverflowingSignaturesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string Signature, int SessionCount)>());

        return new VectorCompactionService(
            sessionStore.Object,
            Options.Create(new BotDetectionOptions()),
            NullLogger<VectorCompactionService>.Instance,
            new NoopSignatureStore(),
            new NoopSessionStore(),
            new NoopIntentStore(),
            coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick1h_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1h);
        sub.Name.Should().Be("VectorCompactionService");
        sub.Hint.Should().Be(CostHint.High);
    }

    [Fact]
    public async Task OnTickAsync_runs_without_throwing_when_outside_compaction_window()
    {
        // The default CompactionHourUtc is 3 (3 AM UTC). Force the tick handler
        // to evaluate at noon UTC -- it should take the "not yet" path and
        // return cleanly without invoking the centroid pruning sequence.
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewService(coordinator);

        var captured = Assert.Single(coordinator.Subscriptions);
        var noon = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        await captured.Handler(noon, CancellationToken.None);

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
