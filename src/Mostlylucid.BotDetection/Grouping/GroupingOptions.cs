using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Grouping;

/// <summary>
///     Tunables for <see cref="IBehaviouralGrouper"/>. Defaults err on the
///     side of NOT grouping -- a missed collapse is recoverable, an
///     incorrect merge of two humans is not.
/// </summary>
public sealed class GroupingOptions
{
    public const string SectionName = "BotDetection:Grouping";

    /// <summary>Master switch. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Bot-only gate threshold. A signature with bot probability below
    ///     this value is treated as human and never grouped. Default 0.5.
    /// </summary>
    public double MinBotProbability { get; set; } = 0.5;

    /// <summary>
    ///     Bot-only gate risk-band floor. Signatures below this band are
    ///     treated as human even if bot probability says otherwise. Default
    ///     <see cref="RiskBand.Low"/>.
    /// </summary>
    public RiskBand MinRiskBand { get; set; } = RiskBand.Low;

    /// <summary>
    ///     Tier 1 -- Identity. Off when Identity:Enabled is false or no
    ///     fingerprint id has been written to the signature.
    /// </summary>
    public bool EnableIdentityTier { get; set; } = true;

    /// <summary>Tier 2 -- Leiden cluster. Off when cluster service isn't running.</summary>
    public bool EnableClusterTier { get; set; } = true;

    /// <summary>
    ///     Minimum cluster member count to form a group. A cluster of 1 is
    ///     no cluster; fall through to a lower tier.
    /// </summary>
    public int ClusterMinMembers { get; set; } = 2;

    /// <summary>
    ///     Minimum Leiden community average-similarity to accept the
    ///     cluster as a grouping. Low cohesion -> fall through to tier 3
    ///     (vector cosine).
    /// </summary>
    public double ClusterMinCohesion { get; set; } = 0.75;

    /// <summary>Tier 3 -- session-vector cosine. Off when SessionVectorSearch isn't wired.</summary>
    public bool EnableVectorTier { get; set; } = true;

    /// <summary>Cosine threshold for tier 3 grouping. Below this, fall through.</summary>
    public double VectorMinCosine { get; set; } = 0.92;

    /// <summary>Tier 4 -- content sequence centroid match.</summary>
    public bool EnableSequenceTier { get; set; } = true;

    /// <summary>Centroid match confidence threshold for tier 4.</summary>
    public double SequenceMinConfidence { get; set; } = 0.85;

    /// <summary>Tier 5 -- /24 + UA family rotation.</summary>
    public bool EnableSubnetRotationTier { get; set; } = true;

    /// <summary>Minimum distinct signatures sharing a /24 to form a rotation group.</summary>
    public int SubnetMinSignatures { get; set; } = 3;

    /// <summary>Minimum distinct UA families across the group (filters real shared-NAT).</summary>
    public int SubnetMinUaFamilies { get; set; } = 3;

    /// <summary>
    ///     Tier 6 -- friendly bot name (current dashboard rule:
    ///     <c>IsGroupableIdentity</c>). Off only if you want pure
    ///     behavioural grouping.
    /// </summary>
    public bool EnableFriendlyNameTier { get; set; } = true;
}
