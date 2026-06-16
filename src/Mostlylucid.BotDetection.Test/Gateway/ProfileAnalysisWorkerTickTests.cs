using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.Common.Scheduling;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Data;
using Stylobot.Gateway.Services;

namespace Mostlylucid.BotDetection.Test.Gateway;

/// <summary>
///     Wave 2 Category B regression coverage for
///     <see cref="ProfileAnalysisWorker"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///     <c>ExecuteAsync</c> ran an unbounded <c>await foreach</c> over a
///     bounded <see cref="ProfileAnalysisChannel"/>; now subscribes to
///     <see cref="TickCadence.Tick10s"/> and each tick drains the channel
///     non-blocking via <see cref="ProfileAnalysisChannel.TryRead"/>.
///     <para>
///         Four facts: subscription shape, tick runs without throwing
///         against an empty channel, dispose releases the subscription
///         (and double-dispose is safe), and the tick drains the channel
///         (each captured snapshot is removed from the queue by the tick
///         handler). Side-effect persistence-store assertions are out of
///         scope here -- the worker relies on the FOSS
///         <c>BlackboardOrchestrator</c> being resolvable from the DI
///         scope, which only stands up under the gateway's full DI graph.
///     </para>
/// </summary>
public sealed class ProfileAnalysisWorkerTickTests
{
    private static ProfileModeOptions Opts(bool enabled = true) => new()
    {
        Enabled = enabled,
        ChannelCapacity = 16,
        Concurrency = 2,
    };

    private static IOptions<BotDetectionOptions> DetectionOptions() =>
        Options.Create(new BotDetectionOptions());

    private static ProfileAnalysisWorker NewWorker(
        RecordingScheduleCoordinator coordinator,
        out ProfileAnalysisChannel channel,
        bool enabled = true)
    {
        var opts = Opts(enabled);
        channel = new ProfileAnalysisChannel(opts);

        // Empty scope factory -- the worker's per-snapshot dispatch resolves
        // BlackboardOrchestrator via GetService and the production code
        // logs + returns null when it's missing. The tests don't run the
        // detection pipeline; they exercise the tick + drain contract.
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        // ProfileCalibrationStore writes to SQLite; redirect to a temp path
        // so test runs don't litter the working tree. Initialisation is the
        // FOSS app's responsibility; the worker takes the store as a
        // dependency.
        var dbPath = Path.Combine(Path.GetTempPath(), $"profile-{Guid.NewGuid():N}.db");
        var store = new ProfileCalibrationStore(dbPath);

        return new ProfileAnalysisWorker(
            scopeFactory,
            channel,
            store,
            Options.Create(opts),
            DetectionOptions(),
            NullLogger<ProfileAnalysisWorker>.Instance,
            coordinator);
    }

    private static ProfileRequestSnapshot NewSnapshot(string requestId) => new()
    {
        RequestId = requestId,
        Method = "GET",
        Path = "/",
        ClientIp = "127.0.0.1",
        UserAgent = "test-agent",
        Headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
        CapturedAt = DateTime.UtcNow,
    };

    [Fact]
    public void Constructor_subscribes_to_Tick10s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewWorker(coordinator, out _);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick10s);
        sub.Name.Should().Be("ProfileAnalysisWorker");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_runs_without_throwing_against_empty_channel()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewWorker(coordinator, out var channel);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
        channel.QueueDepth.Should().Be(0);
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var sut = NewWorker(coordinator, out _);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose must be safe.
        sut.Dispose();
    }

    [Fact]
    public async Task OnTickAsync_drains_channel_when_enabled()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewWorker(coordinator, out var channel);

        channel.TryEnqueue(NewSnapshot("req-a"));
        channel.TryEnqueue(NewSnapshot("req-b"));
        channel.QueueDepth.Should().Be(2);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // Cat-B load-bearing assertion: the tick handler dequeued both
        // snapshots via TryRead. Per-snapshot processing fails silently
        // because BlackboardOrchestrator isn't registered in this test's
        // bare DI graph; that's expected -- the failure is logged inside
        // ProcessSnapshotAsync and does not prevent the drain from
        // completing.
        channel.QueueDepth.Should().Be(0);
    }

    [Fact]
    public async Task OnTickAsync_returns_immediately_when_disabled_without_draining()
    {
        // 5th fact (B5): document early-return behaviour. When
        // ProfileMode.Enabled = false the tick handler returns before the
        // drain pass, so snapshots that landed on the channel stay there.
        // The behaviour is intentional -- the channel's DropOldest
        // bounded-capacity policy bounds memory while ProfileMode is off.
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewWorker(coordinator, out var channel, enabled: false);

        channel.TryEnqueue(NewSnapshot("req-disabled"));
        channel.QueueDepth.Should().Be(1);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // Disabled tick handler does NOT drain.
        channel.QueueDepth.Should().Be(1);
    }
}