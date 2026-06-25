namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Resolved class of the current request's visitor at shed-decision time,
///     derived from the cached fingerprint verdict against the policy's
///     <see cref="ClassGate"/> thresholds. The shed runs before detection so
///     we cannot know the current request's verdict; we use the prior verdict
///     stashed by the verdict cache.
/// </summary>
public enum VisitorClass
{
    /// <summary>
    ///     Prior verdict confidently classified this fingerprint as human.
    ///     Default policy: never shed.
    /// </summary>
    Human = 0,

    /// <summary>
    ///     No prior verdict, borderline prior, or low-confidence prior. Default
    ///     policy: shed at the configured unknown-class fraction.
    /// </summary>
    Unknown = 1,

    /// <summary>
    ///     Prior verdict confidently classified this fingerprint as bot.
    ///     Default policy: always shed when the band escalates.
    /// </summary>
    Bot = 2,
}

/// <summary>
///     Per-policy boundary defining which (prob, conf) tuples count as the
///     human / bot side. Boundaries are INCLUSIVE on both sides:
///     <c>prob &lt;= MaxBotProb</c> on the human side,
///     <c>prob &gt;= MinBotProb</c> on the bot side,
///     <c>conf &gt;= MinConfidence</c> on both. A verdict exactly at the
///     boundary qualifies.
/// </summary>
public sealed record ClassGate(
    double MaxBotProb = 1.0,
    double MinBotProb = 0.0,
    double MinConfidence = 0.0);

/// <summary>
///     Pure static resolver: given the cached prior (prob, conf) and the
///     policy's two gates, returns the visitor class. NaN / infinite / null
///     inputs all degrade to <see cref="VisitorClass.Unknown"/>; the resolver
///     never throws so the caller does not need a try/catch on the hot path.
/// </summary>
public static class ClassGateResolver
{
    public static VisitorClass Resolve(
        double? prob,
        double? conf,
        ClassGate humanGate,
        ClassGate botGate)
    {
        if (prob is null || conf is null) return VisitorClass.Unknown;
        var p = prob.Value;
        var c = conf.Value;
        if (double.IsNaN(p) || double.IsNaN(c) || double.IsInfinity(p) || double.IsInfinity(c))
            return VisitorClass.Unknown;
        if (p <= humanGate.MaxBotProb && c >= humanGate.MinConfidence) return VisitorClass.Human;
        if (p >= botGate.MinBotProb && c >= botGate.MinConfidence) return VisitorClass.Bot;
        return VisitorClass.Unknown;
    }
}
