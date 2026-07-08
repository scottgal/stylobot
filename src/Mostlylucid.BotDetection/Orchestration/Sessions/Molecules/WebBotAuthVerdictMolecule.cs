using Mostlylucid.BotDetection.Auth;

namespace Mostlylucid.BotDetection.Orchestration.Sessions.Molecules;

/// <summary>
///     Projection molecule (Slice 2): encodes / reconstructs a
///     <see cref="WebBotAuthCachedVerdict"/> from a <see cref="Session"/>'s signal
///     log, instead of reading a stored <c>SessionAggregate.WebBotAuthVerdict</c>
///     field. This is the "signals ARE the state" pattern: the verdict is one
///     signal in the session; the reader projects it on demand.
/// </summary>
/// <remarks>
///     Pure + stateless. Encoding is a single delimited signal string (only public
///     metadata + the non-reversible signature digest — never raw auth material,
///     preserving the WBA security invariant). Fields are joined with an ASCII
///     unit-separator (U+001F) which cannot occur in keyids / names / hex hashes;
///     on read the latest matching signal wins (last-write-wins within the window).
/// </remarks>
public static class WebBotAuthVerdictMolecule
{
    /// <summary>Signal prefix that carries the WBA verdict in a session.</summary>
    public const string SignalPrefix = "webbotauth.verdict:";

    // ASCII unit separator: not present in keyids, agent names, algorithm strings,
    // or hex signature digests, so it is a safe, allocation-cheap field delimiter.
    private const char FieldSeparator = '\u001F';

    /// <summary>Encodes a verdict as a single session signal string.</summary>
    public static string ToSignal(WebBotAuthCachedVerdict verdict)
    {
        return string.Concat(
            SignalPrefix,
            verdict.KeyId ?? string.Empty, FieldSeparator,
            verdict.Verdict.ToString(), FieldSeparator,
            verdict.SubjectName ?? string.Empty, FieldSeparator,
            verdict.Algorithm ?? string.Empty, FieldSeparator,
            verdict.SignatureHash ?? string.Empty);
    }

    /// <summary>
    ///     Reconstructs the current verdict for a session by projecting its
    ///     contributions: the latest <c>webbotauth.verdict:</c> signal wins.
    ///     Returns null when the session carries no verdict yet.
    /// </summary>
    public static WebBotAuthCachedVerdict? FromSession(Session session)
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

    private static WebBotAuthCachedVerdict? Parse(string signal)
    {
        var parts = signal.Substring(SignalPrefix.Length).Split(FieldSeparator);
        if (parts.Length != 5) return null;
        if (!Enum.TryParse<TokenOutcome>(parts[1], out var outcome)) return null;

        return new WebBotAuthCachedVerdict(
            KeyId: parts[0],
            Verdict: outcome,
            SubjectName: string.IsNullOrEmpty(parts[2]) ? null : parts[2],
            Algorithm: string.IsNullOrEmpty(parts[3]) ? null : parts[3],
            SignatureHash: string.IsNullOrEmpty(parts[4]) ? null : parts[4]);
    }
}
