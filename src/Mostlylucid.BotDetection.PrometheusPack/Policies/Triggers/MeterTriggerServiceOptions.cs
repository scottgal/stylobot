namespace Mostlylucid.BotDetection.PrometheusPack.Policies.Triggers;

/// <summary>
///     Configuration for <see cref="MeterTriggerService"/>. Wave 5 of the
///     architectural-drift remediation: pulls the previously
///     <c>static readonly</c> magic numbers off the class so deployments can
///     tune the trigger loop without recompiling.
/// </summary>
/// <remarks>
///     The tick cadence itself is owned by
///     <see cref="Mostlylucid.Common.Scheduling.IScheduleCoordinator"/>.
///     The service subscribes to
///     <see cref="Mostlylucid.Common.Scheduling.TickCadence.Tick1s"/> in
///     its constructor and the coordinator's single-flight gate decides whether
///     a slow tick gets skipped. The <see cref="TickInterval"/> field below is
///     documentation parity with the pre-Wave-2 class; the coordinator's
///     cadence is the load-bearing knob, not this options value.
/// </remarks>
public sealed class MeterTriggerServiceOptions
{
    /// <summary>
    ///     Documentation-only mirror of the coordinator's Tick1s cadence
    ///     (1 second). Default: <c>TimeSpan.FromSeconds(1)</c>. Changing this
    ///     here does NOT change the coordinator's actual fire rate; the
    ///     coordinator owns the cadence. Downstream telemetry that wants to
    ///     surface "trigger loop runs every Xs" should read it from here so a
    ///     future cadence retune surfaces in one place.
    /// </summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     How long the trigger service caches the rule-corpus snapshot before
    ///     re-reading from <see cref="Mostlylucid.BotDetection.Policies.Rules.IPolicyRuleStore"/>.
    ///     Default: <c>TimeSpan.FromSeconds(5)</c>. Five ticks at the Tick1s
    ///     cadence is a small enough window that a paid-tier hot-reload still
    ///     surfaces in under a tick of the next 5-second pull, while keeping
    ///     the store off the per-tick hot path.
    /// </summary>
    public TimeSpan RuleListCacheTtl { get; set; } = TimeSpan.FromSeconds(5);
}
