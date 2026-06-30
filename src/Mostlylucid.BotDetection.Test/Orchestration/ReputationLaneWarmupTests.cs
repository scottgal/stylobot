using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Lifecycle;
using Mostlylucid.BotDetection.Orchestration.Lanes;
using Mostlylucid.BotDetection.Orchestration.Signals;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Pins <see cref="ReputationLane"/>'s gateway-warmup short-circuit:
///     during cold-start warmup the lane emits a neutral 0.0 score so the
///     trend / consistency arms (which produce spurious "bot-like" verdicts
///     off the first dozen requests when every signature looks similar by
///     chance) don't compound cold-start noise into the upstream scorer.
///     After warmup, the lane composes existing scores normally.
/// </summary>
public class ReputationLaneWarmupTests
{
    private static OperationCompleteSignal Op(int statusCode, bool honeypot = false)
        => new()
        {
            Signature = "sig",
            RequestId = "rid",
            Timestamp = DateTimeOffset.UtcNow,
            Priority = 0,
            RequestRisk = 0,
            ResponseScore = 0,
            StatusCode = statusCode,
            ResponseBytes = 0,
            CombinedScore = 0,
            Honeypot = honeypot,
            TriggerSignals = new Dictionary<string, object>()
        };

    private static GatewayWarmupGate BuildGate(
        DegradationAtom atom,
        bool warmedUp,
        FixedTimeProvider clock)
    {
        // To produce a warmed-up gate, advance the clock past WarmupDuration
        // and seed atom samples past MinGatewaySamples. To produce a warming
        // gate, leave both below the floor.
        var opts = new GatewayWarmupOptions
        {
            WarmupDuration = TimeSpan.FromSeconds(1),
            MinGatewaySamples = 5,
            MinSignatureSamples = 1
        };
        var gate = new GatewayWarmupGate(atom, Options.Create(opts), clock);
        if (warmedUp)
        {
            for (var i = 0; i < 20; i++) atom.RecordResponse(200, latencyMs: 5, path: "/");
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        return gate;
    }

    private static ReputationLane BuildLane(
        SignalSink sink,
        GatewayWarmupGate? gate)
        => new(sink, "c.test", upstreamHealth: null, gatewayWarmup: gate);

    private static string? LatestSignalValue(SignalSink sink, string signalName)
    {
        string? latest = null;
        foreach (var s in sink.Sense())
            if (s.Signal == signalName)
                latest = s.Key;
        return latest;
    }

    [Fact]
    public async Task Lane_emits_zero_score_when_gateway_is_warming()
    {
        var atom = new DegradationAtom();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));
        var gate = BuildGate(atom, warmedUp: false, clock);
        var sink = new SignalSink(100, TimeSpan.FromMinutes(5));
        var lane = BuildLane(sink, gate);

        var window = new List<OperationCompleteSignal>
        {
            Op(404), Op(404), Op(404), Op(404), Op(404),
            Op(200), Op(200), Op(200), Op(200), Op(200),
        };

        await lane.AnalyzeAsync(window);

        // The lane's score signal is scoped to the coordinator key.
        // During warmup it MUST emit 0.0 regardless of how bot-shaped
        // the window looks.
        var scoreValue = LatestSignalValue(sink, lane.ScopedScoreKey);
        Assert.Equal("0.0000", scoreValue);
    }

    [Fact]
    public async Task Lane_emits_positive_score_after_gateway_warms_up()
    {
        // Regression guard: a heavily 404-shaped window through a warmed
        // gateway must STILL produce a positive reputation score (existing
        // behaviour must be preserved). The warmup gate must short-circuit
        // only during the cold-start window, never after.
        var atom = new DegradationAtom();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));
        var gate = BuildGate(atom, warmedUp: true, clock);
        var sink = new SignalSink(100, TimeSpan.FromMinutes(5));
        var lane = BuildLane(sink, gate);

        var window = new List<OperationCompleteSignal>
        {
            Op(404), Op(404), Op(404), Op(404), Op(404),
            Op(200), Op(200), Op(200), Op(200), Op(200),
        };

        await lane.AnalyzeAsync(window);

        var scoreValue = LatestSignalValue(sink, lane.ScopedScoreKey);
        Assert.NotNull(scoreValue);
        var score = double.Parse(scoreValue!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(score > 0.0, $"expected >0 after warmup, got {score}");
    }

    [Fact]
    public async Task Null_gateway_warmup_falls_back_to_existing_behaviour()
    {
        // FOSS host without the lifecycle module: gate is null, lane must
        // not short-circuit. Existing reputation behaviour preserved.
        var sink = new SignalSink(100, TimeSpan.FromMinutes(5));
        var lane = BuildLane(sink, gate: null);

        var window = new List<OperationCompleteSignal>
        {
            Op(404), Op(404), Op(404), Op(404), Op(404),
            Op(200), Op(200), Op(200), Op(200), Op(200),
        };

        await lane.AnalyzeAsync(window);

        var scoreValue = LatestSignalValue(sink, lane.ScopedScoreKey);
        Assert.NotNull(scoreValue);
        var score = double.Parse(scoreValue!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(score > 0.0,
            $"null warmup gate must NOT short-circuit; got {score}");
    }
}
