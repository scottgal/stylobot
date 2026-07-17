namespace Mostlylucid.BotDetection.Grouping;

/// <summary>
///     Produces a canonical <see cref="GroupKey"/> for any signature on the
///     dashboard. Every display surface that wants to collapse rows
///     consults this one bridge instead of rolling its own rule.
/// </summary>
/// <remarks>
///     <para>
///         The grouper enforces a non-overridable <b>bot-only gate</b>:
///         signatures classified as human (bot probability below the
///         threshold OR risk band below the floor) ALWAYS receive
///         <see cref="GroupKeySource.Human"/> as their group source. Two
///         humans never share a group key, regardless of how
///         behaviourally similar their requests look. The product reason:
///         humans are individuals, not actors-to-track-in-aggregate;
///         collapsing two human rows would destroy per-visitor session
///         paths and is wrong even on shared egress (corporate NAT,
///         mobile CGNAT, university).
///     </para>
///     <para>
///         For bot-shaped signatures, the grouper descends a hierarchy
///         of strength (see <see cref="GroupKeySource"/>). First match
///         wins; lower-tier keys are not consulted once a higher tier
///         fires.
///     </para>
/// </remarks>
public interface IBehaviouralGrouper
{
    /// <summary>
    ///     Resolve the canonical group key for a signature snapshot.
    ///     Returns a non-null <see cref="GroupKey"/> in every case; for
    ///     humans, the source is <see cref="GroupKeySource.Human"/>; for
    ///     unmatchable bots, <see cref="GroupKeySource.RawSignature"/>.
    /// </summary>
    GroupKey Resolve(GroupingInput input);
}

/// <summary>
///     Lightweight value passed to the grouper. Built from a
///     <c>ProjectedVisitor</c>, <c>DashboardTopBotEntry</c>, or any other
///     row-shaped type so consumers don't have to know about each other's
///     view-models. All fields optional except <see cref="Signature"/>.
/// </summary>
public sealed record GroupingInput
{
    public required string Signature { get; init; }
    public double BotProbability { get; init; }
    public string? RiskBand { get; init; }      // string form because view-model types vary
    public bool? IsBot { get; init; }            // detection classifier verdict
    public string? BotName { get; init; }
    public string? BotType { get; init; }
    public string? CustomBotName { get; init; }
    public string? IpSubnetSignature { get; init; }
    public string? UaFamily { get; init; }
    public string? CountryCode { get; init; }
    public string? ClusterId { get; init; }
    public string? FingerprintId { get; init; }
}
