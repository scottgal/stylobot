namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     Configurable thresholds for <see cref="SessionAtom"/>. Bound from
///     <c>BotDetection:Session:Atom</c>.
/// </summary>
public sealed class SessionAtomOptions
{
    public const string SectionName = "BotDetection:Session:Atom";

    /// <summary>
    ///     Minimum sample count in the aggregate before probability /
    ///     client-type shifts trigger persistence. Honeypot hits and
    ///     new-fingerprint cases bypass this floor -- they are always
    ///     shift-worthy on the first sample.
    /// </summary>
    public int MinSamplesToPersist { get; set; } = 3;

    /// <summary>
    ///     Probability delta between the aggregate mean and the persisted
    ///     <see cref="Mostlylucid.BotDetection.Identity.Fingerprint.CachedBotProbability"/>
    ///     that counts as a shift worth persisting. Below this the session
    ///     is treated as consistent with the persisted state.
    /// </summary>
    public double ProbabilityShiftDelta { get; set; } = 0.15;
}