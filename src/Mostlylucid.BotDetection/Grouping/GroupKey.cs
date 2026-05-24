namespace Mostlylucid.BotDetection.Grouping;

/// <summary>
///     Canonical group identifier for a signature, produced by
///     <see cref="IBehaviouralGrouper"/>. Two signatures with the same
///     <see cref="Source"/> + <see cref="Value"/> belong to the same
///     display row.
/// </summary>
/// <remarks>
///     The <see cref="Display"/> field is the operator-facing label that
///     explains WHY the group was formed -- "Leiden cluster c47 (23 members)",
///     "Network rotation 198.51.100.0/24 (47 sigs)", "Googlebot (verified)".
///     Surface it as a chip on the collapsed row.
/// </remarks>
public sealed record GroupKey(
    GroupKeySource Source,
    string Value,
    string Display)
{
    /// <summary>
    ///     Stable scalar key for use in dictionary keys and grouping
    ///     comparisons. Two GroupKeys with equal canonical strings are
    ///     equivalent.
    /// </summary>
    public string Canonical => $"{(int)Source}:{Value}";
}

/// <summary>
///     Source of a group classification. Ordered by strength: earlier
///     values win when multiple tiers could apply. <see cref="Human"/>
///     and <see cref="RawSignature"/> are sentinels meaning "do not
///     collapse this row".
/// </summary>
public enum GroupKeySource
{
    /// <summary>Metastable fingerprint identity (Identity:Enabled = true required).</summary>
    Identity = 1,

    /// <summary>Leiden community cluster with adequate cohesion.</summary>
    Cluster = 2,

    /// <summary>Session-vector cosine over a neighbour-index threshold.</summary>
    SessionVectorCosine = 3,

    /// <summary>Content-sequence centroid (Markov chain) match.</summary>
    SequenceCentroid = 4,

    /// <summary>/24 + UA family rotation cluster.</summary>
    SubnetUaRotation = 5,

    /// <summary>Friendly bot-name match (the existing IsGroupableIdentity rule).</summary>
    FriendlyBotName = 6,

    /// <summary>No behavioural collapse applies -- row renders by itself.</summary>
    RawSignature = 7,

    /// <summary>
    ///     Human visitor -- explicitly NOT grouped, regardless of
    ///     behavioural similarity to anyone else. Sentinel for clarity in
    ///     diagnostics; renders the same as <see cref="RawSignature"/>.
    /// </summary>
    Human = 8
}
