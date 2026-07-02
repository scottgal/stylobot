namespace Mostlylucid.BotDetection.Storage;

/// <summary>
///     Computes an effective entry cap that shrinks as managed-memory load rises,
///     mirroring the runtime GC's own high-memory-load threshold behaviour. A
///     store's configured max is the CEILING; as memory pressure climbs toward the
///     GC's high-load threshold the effective cap ramps down to a <see cref="_floor"/>,
///     so the store sheds its lowest-signal entries <i>before</i> the host is
///     squeezed rather than after. This self-tunes to the box — a few thousand on a
///     4-core Pi, the full ceiling on a 94 GB server — the same way Server GC scales
///     its heap to available RAM.
///     <para>
///         Pressure is <c>MemoryLoadBytes / HighMemoryLoadThresholdBytes</c> from
///         <see cref="GC.GetGCMemoryInfo(GCKind)"/>: 1.0 means the runtime itself
///         considers memory load high. Below <see cref="RampStart"/> the ceiling is
///         used unchanged; between <see cref="RampStart"/> and 1.0 the cap ramps
///         linearly to the floor; at or above 1.0 the floor holds.
///     </para>
/// </summary>
public sealed class MemoryAdaptiveCap
{
    /// <summary>Pressure below which the full configured ceiling is used.</summary>
    private const double RampStart = 0.70;

    private readonly int _configuredMax;
    private readonly int _floor;
    // Injectable so tests can drive arbitrary (load, threshold) points without
    // constructing a GCMemoryInfo (its members are runtime-populated).
    private readonly Func<(long Load, long HighThreshold)> _sample;

    public MemoryAdaptiveCap(
        int configuredMax,
        int floor = 1_000,
        Func<(long Load, long HighThreshold)>? sample = null)
    {
        _floor = Math.Max(1, floor);
        _configuredMax = Math.Max(configuredMax, _floor);
        _sample = sample ?? DefaultSample;
    }

    /// <summary>The configured ceiling, ignoring memory pressure.</summary>
    public int Ceiling => _configuredMax;

    /// <summary>
    ///     Current effective cap given live memory pressure. Cheap enough to call
    ///     per drain/eviction cycle; do NOT call per hot-path write.
    /// </summary>
    public int Effective()
    {
        var (load, high) = _sample();
        // Info unavailable (e.g. some sandboxes report 0) → trust the ceiling.
        if (high <= 0 || load <= 0) return _configuredMax;

        var pressure = (double)load / high;
        if (pressure <= RampStart) return _configuredMax;
        if (pressure >= 1.0) return _floor;

        var t = (pressure - RampStart) / (1.0 - RampStart);   // 0..1 across the ramp
        var scaled = _configuredMax - t * (_configuredMax - _floor);
        return (int)Math.Max(_floor, Math.Round(scaled));
    }

    private static (long, long) DefaultSample()
    {
        var info = GC.GetGCMemoryInfo();
        return (info.MemoryLoadBytes, info.HighMemoryLoadThresholdBytes);
    }
}