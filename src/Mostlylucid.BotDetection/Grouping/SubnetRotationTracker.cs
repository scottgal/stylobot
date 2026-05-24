using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Grouping;

/// <summary>
///     Tracks bot-shaped signatures observed per <c>/24</c> subnet over a
///     rolling window. When a subnet accumulates enough distinct
///     signatures across enough UA families (and stays single-country),
///     the tracker reports a "rotation group" -- the canonical signal for
///     "one actor rotating IPs / UAs from inside a single /24".
/// </summary>
/// <remarks>
///     <para>
///         Purely additive in-memory state -- no SQLite, no DI on hot
///         paths other than this tracker. Bounded LRU by last-touch so
///         attackers rotating across millions of /24s can't OOM the store.
///     </para>
///     <para>
///         The thresholds (min signatures, min UA families, country
///         constancy) come from <see cref="GroupingOptions"/> -- the
///         tracker reads them on every Record call so live config
///         reloads take effect.
///     </para>
/// </remarks>
public sealed class SubnetRotationTracker : ISubnetRotationTracker
{
    public const int MaxSubnets = 10_000;
    private const int MaxSignaturesPerSubnet = 200; // anything larger is "this isn't a single actor anymore"

    private readonly ConcurrentDictionary<string, RotationWindow> _windows = new(StringComparer.Ordinal);

    public SubnetRotationGroup? RecordAndCheck(
        string subnet,
        string signature,
        string? uaFamily,
        string? countryCode,
        int minSignatures,
        int minUaFamilies)
    {
        if (string.IsNullOrEmpty(subnet) || string.IsNullOrEmpty(signature))
            return null;
        if (minSignatures <= 0) return null;

        if (_windows.Count > MaxSubnets) EvictColdest();

        var window = _windows.GetOrAdd(subnet, _ => new RotationWindow());
        return window.RecordAndCheck(signature, uaFamily, countryCode, minSignatures, minUaFamilies);
    }

    public RotationSnapshot? Peek(string subnet)
    {
        return _windows.TryGetValue(subnet, out var w) ? w.Snapshot() : null;
    }

    private void EvictColdest()
    {
        var target = MaxSubnets - (MaxSubnets / 10);
        if (_windows.Count <= target) return;

        var coldest = _windows.OrderBy(kv => kv.Value.LastTouchUtc)
            .Take(_windows.Count - target)
            .Select(kv => kv.Key)
            .ToArray();
        foreach (var k in coldest) _windows.TryRemove(k, out _);
    }

    private sealed class RotationWindow
    {
        // signature -> (uaFamily, countryCode, observedUtc). Capped so a
        // huge real /24 (corporate NAT seeing thousands of employees) can't
        // unbounded-grow our state.
        private readonly Dictionary<string, (string? UaFamily, string? Country, DateTime At)> _signatures = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public DateTime LastTouchUtc { get; private set; } = DateTime.UtcNow;

        public SubnetRotationGroup? RecordAndCheck(
            string signature, string? uaFamily, string? country,
            int minSignatures, int minUaFamilies)
        {
            lock (_lock)
            {
                LastTouchUtc = DateTime.UtcNow;

                if (_signatures.Count >= MaxSignaturesPerSubnet
                    && !_signatures.ContainsKey(signature))
                {
                    // /24 has saturated -- not really a rotation actor,
                    // probably shared NAT.
                    return null;
                }

                _signatures[signature] = (uaFamily, country, LastTouchUtc);

                if (_signatures.Count < minSignatures) return null;

                var uaFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in _signatures.Values)
                {
                    if (!string.IsNullOrEmpty(v.UaFamily)) uaFamilies.Add(v.UaFamily);
                    if (!string.IsNullOrEmpty(v.Country)) countries.Add(v.Country);
                }

                // Rotation must show UA diversity -- a 100-row /24 all
                // running Chrome on Windows is real shared NAT, not an actor.
                if (uaFamilies.Count < minUaFamilies) return null;

                // Rotation must be single-country. Cross-country = different
                // actors using a shared proxy pool; don't collapse.
                if (countries.Count > 1) return null;

                return new SubnetRotationGroup(
                    SignatureCount: _signatures.Count,
                    UaFamilyCount: uaFamilies.Count,
                    Country: countries.FirstOrDefault());
            }
        }

        public RotationSnapshot Snapshot()
        {
            lock (_lock)
            {
                var uaFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in _signatures.Values)
                {
                    if (!string.IsNullOrEmpty(v.UaFamily)) uaFamilies.Add(v.UaFamily);
                    if (!string.IsNullOrEmpty(v.Country)) countries.Add(v.Country);
                }
                return new RotationSnapshot(_signatures.Count, uaFamilies.Count, countries.Count);
            }
        }
    }
}
