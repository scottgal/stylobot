using Mostlylucid.BotDetection.Policies.Signals;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Pumps the live <see cref="PipelineLoadSensor"/> state into the per-request
///     signal vocabulary under the <c>pressure.*</c> family so policy rule
///     predicates can react to system pressure directly, without a metrics
///     pipeline round-trip.
///
///     Signal keys contributed:
///     <list type="bullet">
///       <item><c>pressure.band</c> — string: "Low" / "Normal" / "High" / "Critical"</item>
///       <item><c>pressure.detection_latency_ratio</c> — double: fast EMA over min-baseline</item>
///       <item><c>pressure.upstream_rtt_ratio</c> — double: same shape for upstream RTT</item>
///       <item><c>pressure.threadpool_starved_ticks</c> — int: consecutive 1-second
///           samples with non-zero <see cref="ThreadPool.PendingWorkItemCount"/></item>
///       <item><c>pressure.gen2_per_sec</c> — double: EMA of Gen2 collection rate</item>
///       <item><c>pressure.smoothed_rps</c> — double: 1-second EMA of inbound RPS</item>
///     </list>
///
///     Wired into the FOSS DefaultPolicyResolver via DI: rule predicates can do
///     <c>predicate: "pressure.band == 'Critical'"</c> or
///     <c>predicate: "pressure.upstream_rtt_ratio &gt; 3.0"</c> without any
///     PrometheusPack dependency.
///
///     The sensor is a long-lived singleton with all reads volatile-flagged, so
///     ContributeAsync is allocation-free and lock-free.
/// </summary>
public sealed class PressureSignalContributor : ISignalContributor
{
    private readonly PipelineLoadSensor _sensor;

    public PressureSignalContributor(PipelineLoadSensor sensor) => _sensor = sensor;

    public Task ContributeAsync(IDictionary<string, object?> signals, CancellationToken ct)
    {
        // TryAdd semantics per the ISignalContributor contract: the per-request
        // signal layer wins on conflict. Most of these keys are pressure-only
        // and won't collide, but request-side overrides are still legal.
        signals.TryAdd("pressure.band",                       _sensor.CurrentBand.ToString());
        signals.TryAdd("pressure.detection_latency_ratio",    _sensor.DetectionLatencyRatio);
        signals.TryAdd("pressure.upstream_rtt_ratio",         _sensor.UpstreamRttRatio);
        signals.TryAdd("pressure.threadpool_starved_ticks",   _sensor.ThreadPoolStarvedTicks);
        signals.TryAdd("pressure.gen2_per_sec",               _sensor.Gen2PerSecond);
        signals.TryAdd("pressure.smoothed_rps",               _sensor.SmoothedRps);
        return Task.CompletedTask;
    }
}
