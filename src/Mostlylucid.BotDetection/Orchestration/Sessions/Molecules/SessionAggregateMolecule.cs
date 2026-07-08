using System.Globalization;
using Mostlylucid.BotDetection.Auth;

namespace Mostlylucid.BotDetection.Orchestration.Sessions.Molecules;

/// <summary>
///     Full projection of a <see cref="SessionAggregate"/> to / from a session's
///     signal log. This is what lets <c>SessionStore</c> retire its unbounded
///     per-site aggregate dictionary: the aggregate is no longer a stored object,
///     it is reconstructed on demand from the bounded session's latest snapshot
///     signal (plus the WBA verdict from its own molecule). <see cref="SessionAggregate.RetentionPriority"/>
///     is not stored — it recomputes from the merged state via
///     <see cref="SessionAggregateMerge.ComputeRetentionPriority"/>.
/// </summary>
public static class SessionAggregateMolecule
{
    /// <summary>Signal prefix carrying the full aggregate snapshot.</summary>
    public const string SignalPrefix = "session.aggregate:";

    private const char FieldSeparator = '\u001F';   // unit separator (between fields)   // U+001F unit separator (between fields)
    private const char PairSeparator = '\u001E';    // record separator (between status pairs)    // U+001E record separator (between status pairs)
    private const char KvSeparator = '\u001D';      // group separator (code=count)      // U+001D group separator (code=count)

    /// <summary>Encodes the full aggregate as a single snapshot signal (public state only).</summary>
    public static string ToSignal(SessionAggregate aggregate)
    {
        var statusEncoded = string.Join(
            PairSeparator,
            aggregate.UpstreamStatusCounts.Select(kv =>
                $"{kv.Key.ToString(CultureInfo.InvariantCulture)}{KvSeparator}{kv.Value.ToString(CultureInfo.InvariantCulture)}"));

        return string.Concat(
            SignalPrefix,
            aggregate.FirstSample.UtcTicks.ToString(CultureInfo.InvariantCulture), FieldSeparator,
            aggregate.LastSample.UtcTicks.ToString(CultureInfo.InvariantCulture), FieldSeparator,
            aggregate.SampleCount.ToString(CultureInfo.InvariantCulture), FieldSeparator,
            aggregate.MeanBotProbability.ToString("R", CultureInfo.InvariantCulture), FieldSeparator,
            aggregate.MaxBotProbability.ToString("R", CultureInfo.InvariantCulture), FieldSeparator,
            aggregate.LatestConfidence.ToString("R", CultureInfo.InvariantCulture), FieldSeparator,
            aggregate.HoneypotHits.ToString(CultureInfo.InvariantCulture), FieldSeparator,
            aggregate.DominantClientType ?? string.Empty, FieldSeparator,
            statusEncoded);
    }

    /// <summary>
    ///     Reconstructs the aggregate for a session (latest snapshot wins). The
    ///     fingerprint / site keys come from the caller (the coordinator holds
    ///     them); the WBA verdict is projected from <see cref="WebBotAuthVerdictMolecule"/>;
    ///     retention priority recomputes. Returns null when the session carries no
    ///     aggregate snapshot yet.
    /// </summary>
    public static SessionAggregate? FromSession(Session session, string fingerprintId, string siteId)
    {
        string? latest = null;
        var latestAt = DateTimeOffset.MinValue;

        foreach (var contribution in session.SnapshotContributions())
        {
            foreach (var signal in contribution.Signals)
            {
                if (signal.Length <= SignalPrefix.Length
                    || !signal.StartsWith(SignalPrefix, StringComparison.Ordinal))
                    continue;

                if (contribution.At >= latestAt)
                {
                    latestAt = contribution.At;
                    latest = signal;
                }
            }
        }

        if (latest is null) return null;

        var parts = latest.Substring(SignalPrefix.Length).Split(FieldSeparator);
        if (parts.Length != 9) return null;

        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var firstTicks)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastTicks)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleCount)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var mean)
            || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var max)
            || !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence)
            || !int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var honeypotHits))
            return null;

        var statusCounts = ParseStatusCounts(parts[8]);
        var wba = WebBotAuthVerdictMolecule.FromSession(session);

        return new SessionAggregate
        {
            FingerprintId = fingerprintId,
            SiteId = siteId,
            FirstSample = new DateTimeOffset(firstTicks, TimeSpan.Zero),
            LastSample = new DateTimeOffset(lastTicks, TimeSpan.Zero),
            SampleCount = sampleCount,
            MeanBotProbability = mean,
            MaxBotProbability = max,
            LatestConfidence = confidence,
            HoneypotHits = honeypotHits,
            UpstreamStatusCounts = statusCounts,
            DominantClientType = string.IsNullOrEmpty(parts[7]) ? null : parts[7],
            WebBotAuthVerdict = wba,
            RetentionPriority = SessionAggregateMerge.ComputeRetentionPriority(mean, confidence, honeypotHits),
        };
    }

    private static IReadOnlyDictionary<int, int> ParseStatusCounts(string encoded)
    {
        var result = new Dictionary<int, int>();
        if (encoded.Length == 0) return result;

        foreach (var pair in encoded.Split(PairSeparator))
        {
            var kv = pair.Split(KvSeparator);
            if (kv.Length == 2
                && int.TryParse(kv[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
                && int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                result[code] = count;
        }

        return result;
    }
}
