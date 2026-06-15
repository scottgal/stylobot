namespace Mostlylucid.BotDetection.Test.Scheduling.Helpers;

/// <summary>
///     Frozen <see cref="TimeProvider"/> for deterministic tick-fan-out tests.
///     We don't need to advance time (the schedule coordinator's
///     <c>TickOnceAsync</c> drives the loop directly), so a fixed-now provider
///     is enough and lets the FOSS test project skip the
///     <c>Microsoft.Extensions.TimeProvider.Testing</c> package reference.
///     <para>
///         <b>Wave 2 migration helper.</b> Promoted from private nested classes
///         in <c>ScheduleCoordinatorTests</c> and
///         <c>PolicyStackSummaryBuilderTests</c> -- every per-service migration
///         PR that drives a real schedule coordinator through
///         <c>TickOnceAsync</c> needs a frozen clock; this is the shared shape.
///     </para>
/// </summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    /// <summary>Construct with the frozen instant the provider should return.</summary>
    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>
    ///     Advance the frozen clock. Most tests don't need this -- they drive
    ///     the schedule coordinator through <c>TickOnceAsync</c> instead -- but
    ///     tests that exercise rule-cache TTLs or "is now past X?" gating need
    ///     to nudge the clock forward.
    /// </summary>
    public void Advance(TimeSpan by) => _now = _now.Add(by);

    /// <summary>
    ///     Set the frozen clock to a specific instant. Use when a test needs
    ///     to assert "given now=T, did the handler fire?" without computing
    ///     deltas.
    /// </summary>
    public void SetUtcNow(DateTimeOffset to) => _now = to;
}