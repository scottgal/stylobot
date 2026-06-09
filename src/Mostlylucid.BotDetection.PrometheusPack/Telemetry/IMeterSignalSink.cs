namespace Mostlylucid.BotDetection.PrometheusPack.Telemetry;

/// <summary>
///     Extension point for downstream consumers of <see cref="MeterSignal" />
///     materialisations. <see cref="LocalMeterStream" /> emits a signal whenever
///     a tracked meter materially updates (subject to
///     <c>LocalMeterStreamOptions.MinSignalEmitInterval</c> coalescing).
/// </summary>
/// <remarks>
///     <para>
///         Phase E (this commit) ships <see cref="NullMeterSignalSink" /> as the
///         default registration; no consumers are wired yet. Phase F replaces it
///         with a blackboard adapter so per-request predicates can read live
///         meter values. Phase G adds the oscilloscope-style trigger service.
///     </para>
///     <para>
///         Implementations MUST be cheap and non-blocking. <see cref="OnMeter" />
///         runs on the measurement callback thread; expensive work (DB writes,
///         outbound HTTP) belongs in a downstream channel + drainer.
///     </para>
/// </remarks>
public interface IMeterSignalSink
{
    /// <summary>
    ///     Called when <see cref="LocalMeterStream" /> emits a materialised meter
    ///     signal. Implementations must not throw -- exceptions on the listener
    ///     thread can sink the entire MeterListener.
    /// </summary>
    void OnMeter(MeterSignal signal);
}

/// <summary>
///     Default no-op <see cref="IMeterSignalSink" />. Registered by
///     <c>AddBotDetectionTelemetry</c> via <c>TryAddSingleton</c> so commercial
///     packs (or Phase F's blackboard adapter) can override with a real sink.
/// </summary>
public sealed class NullMeterSignalSink : IMeterSignalSink
{
    /// <inheritdoc />
    public void OnMeter(MeterSignal signal)
    {
        // No-op by design.
    }
}
