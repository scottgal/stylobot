using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.HealthEndpoints;

/// <summary>
///     Static helper that decides whether an inbound request has the shape of an
///     automated health probe rather than a human browser navigation.
/// </summary>
/// <remarks>
///     <para>
///         "Probe shape" is a POSITIVE match: the raw User-Agent must contain at
///         least one family token from <c>BotDetection:HealthEndpoints:ProbeUserAgents</c>
///         (defaults: <c>kube-probe</c>, <c>Go-http-client</c>, <c>curl</c>,
///         <c>wget</c>, <c>docker</c>) AND the request must NOT carry a browser
///         navigation signal (<c>Sec-Fetch-Mode: navigate</c>).
///     </para>
///     <para>
///         Both conditions are required together so that:
///         - A curl UA with <c>Sec-Fetch-Mode: navigate</c> (injected by a proxy) is
///           rejected (navigation signal wins).
///         - A Chrome browser UA without any probe-family token is rejected (no positive
///           UA match).
///         - An empty UA is also rejected: "no UA" is indeterminate, not a confirmed probe.
///     </para>
///     <para>
///         Signal sources: <see cref="SignalSink.ReadHint"/> is preferred (production
///         path where atoms raise signals into the sink). The
///         <paramref name="signals"/> dictionary is the fallback for callers (unit tests)
///         that hand-build a <c>premergedSignals</c> dictionary.
///     </para>
/// </remarks>
public static class ProbeShapeClassifier
{
    /// <summary>
    ///     Returns <see langword="true"/> when the request carries probe shape:
    ///     a positive User-Agent family match AND no browser-navigation signal.
    /// </summary>
    /// <param name="signals">
    ///     Pre-merged signals dictionary (may be empty in the full-sink production path).
    /// </param>
    /// <param name="sink">
    ///     Live signal sink from the current request; preferred source for UA and
    ///     Sec-Fetch-Mode. May be <see langword="null"/> in unit tests.
    /// </param>
    /// <param name="probeUserAgents">
    ///     Case-insensitive family tokens to match against the raw UA. An empty list
    ///     produces <see langword="false"/> for every input.
    ///     Use <see cref="HealthEndpointOptions.DefaultProbeUserAgents"/> when no
    ///     configured override is available.
    /// </param>
    public static bool IsProbeShape(
        IReadOnlyDictionary<string, object> signals,
        SignalSink? sink,
        IReadOnlyList<string> probeUserAgents)
    {
        if (probeUserAgents.Count == 0) return false;

        // UA: sink-first (production), dict fallback (tests).
        var rawUa = sink?.ReadHint(SignalKeys.UserAgent)
                    ?? (signals.TryGetValue(SignalKeys.UserAgent, out var uaV) ? uaV as string : null)
                    ?? string.Empty;

        if (string.IsNullOrEmpty(rawUa)) return false;

        // Browser-navigation shape guard: Sec-Fetch-Mode == "navigate" is a
        // definitive browser-document-navigation signal. No health probe sends this.
        // Note: Accept: text/html would be a useful second exclusion here, but
        // the signal pipeline exposes only header.has_accept (bool), not the Accept
        // header value. Sec-Fetch-Mode:navigate is the reliable browser-nav signal.
        var secFetchMode = sink?.ReadHint(SignalKeys.HeaderSecFetchMode)
                           ?? (signals.TryGetValue(SignalKeys.HeaderSecFetchMode, out var sfmV)
                               ? sfmV as string
                               : null);
        if (string.Equals(secFetchMode, "navigate", StringComparison.OrdinalIgnoreCase))
            return false;

        // Positive probe-UA match (case-insensitive contains).
        foreach (var family in probeUserAgents)
        {
            if (!string.IsNullOrEmpty(family)
                && rawUa.Contains(family, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
