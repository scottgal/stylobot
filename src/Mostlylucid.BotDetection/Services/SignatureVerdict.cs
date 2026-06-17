using Mostlylucid.BotDetection.Orchestration;
using DetectionContribution = Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionContribution;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Immutable snapshot of a primary signature's per-fingerprint state as held in
///     <see cref="Mostlylucid.BotDetection.Orchestration.SignatureCoordinator"/>'s
///     sliding window. The verdict is the live source of truth for the
///     <c>SignatureVerdictGate</c>: it does NOT come from a parallel cache and is
///     always derived from whatever the coordinator currently holds.
/// </summary>
public sealed record SignatureVerdict
{
    public required string SignatureId { get; init; }

    /// <summary>EWMA / running bot probability across this signature's observed requests.</summary>
    public required double BotProbability { get; init; }

    /// <summary>Confidence in the bot probability; usually grows with sample size.</summary>
    public required double Confidence { get; init; }

    /// <summary>Latest risk band classification. May be <see cref="RiskBand.Unknown"/> if not yet observed.</summary>
    public RiskBand RiskBand { get; init; } = RiskBand.Unknown;

    /// <summary>Latest threat (intent) score, orthogonal to BotProbability.</summary>
    public double ThreatScore { get; init; }

    /// <summary>Total observed requests for this signature in the window.</summary>
    public int RequestCount { get; init; }

    /// <summary>When the coordinator last observed this signature. Used by the gate for freshness.</summary>
    public DateTime LastSeenUtc { get; init; }

    /// <summary>
    ///     When non-null, the verdict was sourced from (or composed with) the metastable
    ///     fingerprint cache via this fingerprint id. Lets dashboards and headers reveal
    ///     when identity-layer state — not the per-signature aggregate — drove the gate.
    /// </summary>
    public string? IdentityFingerprintId { get; init; }

    /// <summary>
    ///     True when the cached fingerprint verdict was the FRESHER source and the
    ///     verdict carries the fingerprint's probability rather than the per-signature
    ///     aggregate. Implies the visitor's identity has been stable across rotation
    ///     even if their primary signature has changed.
    /// </summary>
    public bool FromIdentityCache { get; init; }

    /// <summary>
    ///     Contributions from the most recent pipeline-running observation. Carried on
    ///     the verdict so the verdict-gate Skip path can rebuild the dashboard's
    ///     detector_contributions chips on a cache hit -- without this, every
    ///     Skip-served detection rendered with an empty contributions panel on the
    ///     signature detail page (the WHOLE-point-of-the-system signal disappeared).
    ///     <para>
    ///     Null when this verdict was synthesised from the identity cache (no
    ///     contribution history exists at that layer) or when no pipeline-running
    ///     request has been observed for this signature yet.
    ///     </para>
    /// </summary>
    public IReadOnlyList<DetectionContribution>? LatestContributions { get; init; }

    /// <summary>
    ///     Signals snapshot from the most recent pipeline-running observation. The Skip
    ///     path's <c>cachedSignals</c> dict carried only PrimarySignature + UserAgent
    ///     bot type/name; rebuilding the dashboard's important_signals chip set without
    ///     this means every Skip-served row showed a near-empty signals panel even
    ///     when the underlying pipeline pass had populated dozens of detector signals
    ///     (TLS JA4, header order, archetype anchor, drift slot, etc.).
    /// </summary>
    public IReadOnlyDictionary<string, object>? LatestSignals { get; init; }
}
