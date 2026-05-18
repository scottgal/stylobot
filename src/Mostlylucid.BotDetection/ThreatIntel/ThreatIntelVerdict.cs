namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     One verdict from one provider for one subject. Producers normalise their
///     vendor-specific response into this shape; raw fields go in
///     <see cref="Metadata"/> for dashboards / debug views.
/// </summary>
/// <remarks>
///     Confidence is provider-normalised to [0,1]. The contributor's
///     <c>threatintel.score</c> signal takes the max across providers - any one
///     high-confidence verdict dominates.
///     <para>
///     <see cref="ExpiresUtc"/> is checked by <see cref="IThreatIntelCoordinator.Lookup"/>;
///     past-expiry verdicts are filtered out so stale intel doesn't survive a
///     provider's refresh cadence.
///     </para>
/// </remarks>
public sealed record ThreatIntelVerdict
{
    /// <summary>Provider name, e.g. <c>"spamhaus-drop"</c>, <c>"greynoise"</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>
    ///     Classification label. Conventional values:
    ///     <c>malicious</c>, <c>benign</c>, <c>noise</c>, <c>scanner</c>,
    ///     <c>tor</c>, <c>kev</c>, <c>cloud:&lt;vendor&gt;</c> (e.g. <c>cloud:aws</c>).
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    ///     What <em>kind</em> of risk prior this is. Drives the contributor's
    ///     per-class weighting separately from the scalar <see cref="Confidence"/>.
    ///     Lets policy treat a Vulnerability verdict at a sensitive endpoint very
    ///     differently from the same verdict at a static asset, without conflating
    ///     "how confident is the provider" with "how dangerous is this signal".
    /// </summary>
    public IntelligenceSignalClass IntelligenceClass { get; init; } = IntelligenceSignalClass.Unknown;

    /// <summary>Normalised to [0,1]. Map vendor scores in the provider adapter.</summary>
    public double Confidence { get; init; }

    /// <summary>When the upstream produced this verdict (vendor-provided when available, otherwise fetch time).</summary>
    public DateTime ObservedUtc { get; init; }

    /// <summary>
    ///     Hard expiry; the coordinator filters past-expiry verdicts on read.
    ///     Offline providers set this to the next planned refresh cycle; live providers
    ///     set it to a vendor-recommended TTL or the in-cache TTL, whichever is shorter.
    /// </summary>
    public DateTime ExpiresUtc { get; init; }

    /// <summary>
    ///     Vendor-native fields preserved for dashboards / debug. Stringly-typed because
    ///     the dashboard renders them as a flat list and the coordinator doesn't need to
    ///     reason about them.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
