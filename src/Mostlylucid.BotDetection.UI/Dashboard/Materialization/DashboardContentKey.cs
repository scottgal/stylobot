using Mostlylucid.BotDetection.UI.Dashboard.Composition;

namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     The <c>SlidingCacheAtom</c> key for a materialized page bundle:
///     <c>(pageKey, tick, envelope)</c> per the ratified materialization shape.
///
///     <para>
///         The key CARRIES the compute inputs (<see cref="Manifest"/>,
///         <see cref="Window"/>) so the cache's factory can compose the bundle on a
///         cold miss — the normalized <see cref="DashboardContentEnvelope"/> is lossy
///         and can't reconstruct them. But cache IDENTITY (Equals/GetHashCode) is the
///         (envelope, tick) ONLY, so a request-path read and the tick materializer
///         that pass the same (manifest, window, tick) collide on one entry, and two
///         sub-bucket-different windows in the same tick share it.
///     </para>
/// </summary>
public sealed class DashboardContentKey : IEquatable<DashboardContentKey>
{
    public DashboardPageManifest Manifest { get; }
    public DashboardPageWindow Window { get; }
    public long Tick { get; }

    /// <summary>The normalized filter/time identity — the equality basis together with <see cref="Tick"/>.</summary>
    public DashboardContentEnvelope Envelope { get; }

    public DashboardContentKey(DashboardPageManifest manifest, DashboardPageWindow window, long tick)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Window = window ?? throw new ArgumentNullException(nameof(window));
        Tick = tick;
        Envelope = DashboardContentEnvelope.From(manifest, window);
    }

    public bool Equals(DashboardContentKey? other)
        => other is not null && Tick == other.Tick && Envelope == other.Envelope;

    public override bool Equals(object? obj) => Equals(obj as DashboardContentKey);

    public override int GetHashCode() => HashCode.Combine(Tick, Envelope);

    public override string ToString() => $"{Envelope.PageKey}@{Tick}";
}
