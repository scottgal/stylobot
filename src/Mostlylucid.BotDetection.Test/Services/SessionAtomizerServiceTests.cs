using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Wave 2 batch 1 regression coverage for
///     <see cref="SessionAtomizerService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     <c>Task.Delay(AtomizerRunInterval)</c> loop and a shutdown-time
///     force-flush; now subscribes to <see cref="TickCadence.Tick1m"/> and
///     gates the atomize pass on "last-success older than configured
///     interval".
/// </summary>
public sealed class SessionAtomizerServiceTests
{
    private static SessionAtomizerService NewService(
        RecordingScheduleCoordinator coordinator)
    {
        var store = new Mock<ISessionStore>(MockBehavior.Loose);
        // No unatomized requests -- the atomize pass short-circuits cleanly.
        store.Setup(s => s.GetUnatomizedRequestsAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PersistedRequest>());

        return new SessionAtomizerService(
            store.Object,
            NullLogger<SessionAtomizerService>.Instance,
            new TestOptionsMonitor<BotDetectionOptions>(new BotDetectionOptions()),
            coordinator);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public void Constructor_subscribes_to_Tick1m_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1m);
        sub.Name.Should().Be("SessionAtomizerService");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_runs_without_throwing_when_no_unatomized_requests()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewService(coordinator);

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

    /// <summary>
    ///     Guardrail for the canonical IsBot semantics on the session write path.
    ///     Five requests at avgBot=0.6 would have produced IsBot=true under the bespoke
    ///     <c>avgBot &gt; 0.5</c> rule; canonical
    ///     <see cref="Risk.SignatureRiskVerdictComposer.IsBot"/>
    ///     requires <c>avgBot &gt;= ClassificationOptions.BotFloor</c> (default 0.70),
    ///     so IsBot must be false. Prevents regression to divergent classification
    ///     thresholds in the session tier.
    /// </summary>
    [Fact]
    public async Task SessionWrite_UsesCanonicalIsBot_BelowBotFloor()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var store       = new Mock<ISessionStore>(MockBehavior.Loose);

        // Five requests, all avgBot=0.6, well older than the SessionGraceAge (35 min
        // default) so the group is force-flushed at atomize time.
        var oldTs = DateTime.UtcNow - TimeSpan.FromHours(1);
        var requests = Enumerable.Range(0, 5).Select(i => new PersistedRequest
        {
            Id             = i + 1,
            Signature      = "test_sig",
            Timestamp      = oldTs.AddSeconds(i),
            Path           = "/",
            MarkovState    = nameof(RequestState.PageView),
            StatusCode     = 200,
            BotProbability = 0.6,
            Confidence     = 0.8,
            RiskBand       = "Medium",
            ProcessingMs   = 1.0,
        }).ToList();

        store.Setup(s => s.GetUnatomizedRequestsAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(requests);

        PersistedSession? captured = null;
        store.Setup(s => s.AddSessionAsync(
                It.IsAny<RequestScope>(),
                It.IsAny<PersistedSession>(),
                It.IsAny<CancellationToken>()))
            .Callback<RequestScope, PersistedSession, CancellationToken>((_, s, _) => captured = s)
            .ReturnsAsync(42L);

        using var sut = new SessionAtomizerService(
            store.Object,
            NullLogger<SessionAtomizerService>.Instance,
            new TestOptionsMonitor<BotDetectionOptions>(new BotDetectionOptions()),
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        await sub.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Should().NotBeNull("the atomizer should write a session for the group");
        captured!.AvgBotProbability.Should().BeApproximately(0.6, 1e-9);
        captured.IsBot.Should().BeFalse(
            "canonical IsBot requires avgBot >= BotFloor (default 0.70); 0.6 must NOT count as bot");
    }
}
