using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Default in-process implementation of <see cref="IActiveUpstreamProbeState"/>,
///     backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed on
///     upstream name. The active probe tick writes into this store; the gate
///     reads from it. Registered as a singleton so both consumers share the
///     same instance.
/// </summary>
public sealed class ActiveUpstreamProbeState : IActiveUpstreamProbeState
{
    private const string Unhealthy = "unhealthy";

    private readonly ConcurrentDictionary<string, ActiveProbeSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void Update(string upstreamKey, ActiveProbeSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrEmpty(upstreamKey);
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshots[upstreamKey] = snapshot;
    }

    /// <inheritdoc/>
    public ActiveProbeSnapshot? Latest(string upstreamKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(upstreamKey);
        return _snapshots.TryGetValue(upstreamKey, out var snap) ? snap : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    ///     The worst-case fold is deliberate: see <see cref="IActiveUpstreamProbeState.AggregateHealthy"/>
    ///     for the rationale. Implementation: iterate once, return false on
    ///     the first <c>"unhealthy"</c> entry, true if at least one entry
    ///     was seen and none were unhealthy, null if the dictionary is empty.
    /// </remarks>
    public bool? AggregateHealthy()
    {
        var seen = false;
        foreach (var snap in _snapshots.Values)
        {
            seen = true;
            if (string.Equals(snap.Status, Unhealthy, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return seen ? true : null;
    }
}
