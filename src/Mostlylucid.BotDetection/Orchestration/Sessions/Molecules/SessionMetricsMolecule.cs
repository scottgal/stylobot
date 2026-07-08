using System.Globalization;

namespace Mostlylucid.BotDetection.Orchestration.Sessions.Molecules;

/// <summary>
///     The scalar session metrics a reader (or the persistence summary) projects
///     out of a session. Mirrors the decision-relevant fields of the legacy
///     <see cref="SessionAggregate"/> without being a stored aggregate object.
/// </summary>
public sealed record SessionMetrics(
    int SampleCount,
    double MeanBotProbability,
    double MaxBotProbability,
    double LatestConfidence,
    int HoneypotHits,
    string? DominantClientType);

/// <summary>
///     Projection molecule (Slice 2/3): encodes the scalar session metrics as a
///     single session signal and reconstructs <see cref="SessionMetrics"/> from a
///     <see cref="Session"/>'s contributions. Same "signals ARE the state" template
///     as <see cref="WebBotAuthVerdictMolecule"/> — the bridge emits a metrics
///     snapshot per request (latest-wins); readers and the persistence summary
///     project it on demand, so there is no stored <c>SessionAggregate</c> object.
/// </summary>
public static class SessionMetricsMolecule
{
    /// <summary>Signal prefix carrying the session metrics snapshot.</summary>
    public const string SignalPrefix = "session.metrics:";

    // ASCII unit separator: absent from numbers and client-type strings.
    private const char FieldSeparator = '\u001F';

    /// <summary>Encodes the aggregate's current scalar metrics as a snapshot signal.</summary>
    public static string ToSignal(SessionAggregate aggregate)
    {
        return string.Concat(
            SignalPrefix,
            aggregate.SampleCount.ToString(CultureInfo.InvariantCulture), FieldSeparator,
            aggregate.MeanBotProbability.ToString("R", CultureInfo.InvariantCulture), FieldSeparator,
            aggregate.MaxBotProbability.ToString("R", CultureInfo.InvariantCulture), FieldSeparator,
            aggregate.LatestConfidence.ToString("R", CultureInfo.InvariantCulture), FieldSeparator,
            aggregate.HoneypotHits.ToString(CultureInfo.InvariantCulture), FieldSeparator,
            aggregate.DominantClientType ?? string.Empty);
    }

    /// <summary>
    ///     Reconstructs the current metrics for a session (latest snapshot wins).
    ///     Returns null when the session carries no metrics signal yet.
    /// </summary>
    public static SessionMetrics? FromSession(Session session)
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

        return latest is null ? null : Parse(latest);
    }

    private static SessionMetrics? Parse(string signal)
    {
        var parts = signal.Substring(SignalPrefix.Length).Split(FieldSeparator);
        if (parts.Length != 6) return null;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleCount)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mean)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var max)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence)
            || !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var honeypotHits))
            return null;

        return new SessionMetrics(
            SampleCount: sampleCount,
            MeanBotProbability: mean,
            MaxBotProbability: max,
            LatestConfidence: confidence,
            HoneypotHits: honeypotHits,
            DominantClientType: string.IsNullOrEmpty(parts[5]) ? null : parts[5]);
    }
}
