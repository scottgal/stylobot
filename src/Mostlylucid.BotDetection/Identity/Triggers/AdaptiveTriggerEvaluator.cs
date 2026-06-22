namespace Mostlylucid.BotDetection.Identity.Triggers;

/// <summary>
///     Pure-function evaluator. Subscribers call <see cref="Evaluate"/> on
///     every heartbeat with their own context and policy. Stateless; the
///     subscriber owns <see cref="TriggerContext.LastSuccessfulRunUtc"/> and
///     the signal source. Thread-safe by virtue of being immutable.
///
///     <para>
///         Algorithm in one place, in evaluation order:
///         <list type="number">
///             <item>Disabled policy &#x2192; NO.</item>
///             <item>Elapsed &lt; <see cref="AdaptiveTriggerPolicy.MinInterval"/> &#x2192; NO (floor).</item>
///             <item><see cref="AdaptiveTriggerPolicy.MaxInterval"/> set AND elapsed &#x2265; max &#x2192; YES (safety net).</item>
///             <item>Current band &gt; <see cref="AdaptiveTriggerPolicy.MaxBand"/> &#x2192; NO (ceiling).</item>
///             <item>Any of <see cref="AdaptiveTriggerPolicy.AnyOfSignals"/> met &#x2192; YES (pressure).</item>
///             <item>Otherwise &#x2192; NO (nothing to do).</item>
///         </list>
///     </para>
/// </summary>
public static class AdaptiveTriggerEvaluator
{
    public static TriggerDecision Evaluate(AdaptiveTriggerPolicy policy, TriggerContext context)
    {
        if (!policy.Enabled)
            return TriggerDecision.NoBecause("disabled");

        // LastSuccessfulRunUtc == null is "never ran". Treat as infinitely overdue so the
        // first heartbeat after startup can fire (subject to other gates). Min-interval
        // gate is therefore skipped on first run -- exactly the demo path we want.
        var elapsed = context.LastSuccessfulRunUtc is { } last
            ? context.Now - last
            : TimeSpan.MaxValue;

        if (elapsed < policy.MinInterval)
            return TriggerDecision.NoBecause(
                $"min interval {Fmt(policy.MinInterval)} not elapsed (have {Fmt(elapsed)})");

        if (policy.MaxInterval is { } max && elapsed >= max)
            return TriggerDecision.YesBecause(
                $"safety net: elapsed {Fmt(elapsed)} >= max {Fmt(max)}");

        if (context.CurrentBand > policy.MaxBand)
            return TriggerDecision.NoBecause(
                $"band {context.CurrentBand} > ceiling {policy.MaxBand}");

        foreach (var cond in policy.AnyOfSignals)
        {
            if (!context.Signals.TryGetValue(cond.Key, out var v)) continue;
            if (v >= cond.Threshold)
                return TriggerDecision.YesBecause(
                    $"signal {cond.Key}={v:F2} >= {cond.Threshold:F2}");
        }

        return TriggerDecision.NoBecause("no signal condition met");
    }

    // Human-readable elapsed: "47s", "2.3m", "1.4h". Keeps the reason string short
    // for the /admin/learning/health endpoint without losing precision.
    private static string Fmt(TimeSpan ts)
    {
        if (ts == TimeSpan.MaxValue) return "infinite";
        if (ts.TotalHours >= 1) return $"{ts.TotalHours:F1}h";
        if (ts.TotalMinutes >= 1) return $"{ts.TotalMinutes:F1}m";
        return $"{ts.TotalSeconds:F0}s";
    }
}
