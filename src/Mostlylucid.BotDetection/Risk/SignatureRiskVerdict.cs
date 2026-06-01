using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Risk;

/// <summary>
///     Operator-facing label that describes the BEHAVIOURAL shape behind the verdict.
///     Orthogonal to <see cref="RiskBand"/> and <see cref="ThreatBand"/>: those measure
///     bot probability + hostile intent; this measures how the behaviour relates to
///     the actor archetypes the system already knows about.
/// </summary>
public enum RiskProfileLabel
{
    /// <summary>No archetype, no verification, no cluster -- can't say anything yet.</summary>
    Unknown = 0,

    /// <summary>
    ///     Current behaviour aligns with the archetype the matcher anchored at; no
    ///     drift, no contradictory signals. The "boring" case.
    /// </summary>
    Aligned,

    /// <summary>
    ///     Current centroid has drifted from the origin archetype past the configured
    ///     cosine delta. The actor is behaving like a different family now than at
    ///     allocation -- friendly anchors revoke themselves once drift is large
    ///     enough to flip the friendly pin off.
    /// </summary>
    Drifting,

    /// <summary>
    ///     Member of a verified-friendly community: NodeInfo-confirmed fediverse
    ///     fanout, vendor-IP-confirmed search engine, RSS reader burst. Threat band
    ///     clamps to None regardless of bot probability; risk is measured against
    ///     the cluster centroid, not the human-norm baseline.
    /// </summary>
    VerifiedCommunity,

    /// <summary>
    ///     Confirmed bad: reputation aborted, honeypot hit, hostile cluster member,
    ///     or raw threat score above the policy gate. Friendly signals are ignored
    ///     because behaviour wins over identity claims when the negative signal is
    ///     crisp.
    /// </summary>
    Hostile
}

/// <summary>
///     Everything the verdict composer needs in one record. Built once per signature
///     per read-cycle by joining the per-request sigmoid roll-up with the
///     cross-request behavioural inferences (cluster type, archetype anchor + drift,
///     friendly / hostile latches). Consumed by <see cref="SignatureRiskVerdictComposer"/>.
///     <para>
///     This record exists so the four operator-facing axes (<see cref="RiskBand"/>,
///     <see cref="ThreatBand"/>, <see cref="RiskProfileLabel"/>, justification text)
///     can be derived in ONE place from ONE input shape -- the patchiness today is
///     that each axis pulls a different evidence slice from a different site.
///     </para>
/// </summary>
public sealed record SignatureRiskInputs
{
    public required string PrimarySignature { get; init; }

    // === per-request scoring (sigmoid roll-up of detector contributions) ===
    public required double BotProbability { get; init; }
    public required double Confidence { get; init; }
    public required double RawThreatScore { get; init; }
    public ThreatBand? RawThreatBand { get; init; }

    // === identity-level latches (any-time-true) ===
    public required bool FriendlyVerified { get; init; }
    public required bool ConfirmedBad { get; init; }
    public required bool DeclaredBot { get; init; }

    // === identity / kind ===
    public string? BotName { get; init; }
    public string? BotType { get; init; }
    public bool IsFriendlyBotType { get; init; }

    // === archetype anchor + drift ===
    public string? OriginArchetypeId { get; init; }
    public string? CurrentArchetypeId { get; init; }
    public double? OriginArchetypeScore { get; init; }
    public double? CurrentArchetypeScore { get; init; }
    public bool IsFriendlyArchetype { get; init; }

    // === cluster membership (latest Leiden snapshot) ===
    public BotClusterType? ClusterType { get; init; }
    public string? ClusterId { get; init; }
    public string? ClusterLabel { get; init; }
    public double? ClusterAverageThreatScore { get; init; }
}

/// <summary>
///     The composed verdict. Single source of truth for the four operator-facing risk
///     axes; every read-path that today computes its own band derivation will read
///     <see cref="RiskBand"/> / <see cref="ThreatBand"/> off this record instead.
/// </summary>
public sealed record SignatureRiskVerdict
{
    public required string PrimarySignature { get; init; }
    public required RiskBand RiskBand { get; init; }
    public required ThreatBand ThreatBand { get; init; }
    public required RiskProfileLabel RiskProfile { get; init; }
    public required string Justification { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>True when the friendly-pin clamped at least one axis.</summary>
    public required bool FriendlyPinFired { get; init; }

    /// <summary>True when the hostile-pin clamped at least one axis.</summary>
    public required bool HostilePinFired { get; init; }
}
