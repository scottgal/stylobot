using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Flags suspicious shifts in the surface dimensions of a matched fingerprint:
///     geo country, ASN, UA family, and the introduction of datacenter / Tor IP space.
///
///     The motivating use case is API-key / cookie theft: the matched identity is
///     stable, but the network and client characteristics around it have changed
///     in ways that are very unusual for a single genuine visitor.
///
///     This is a FOSS stub - it writes <c>risk.*</c> signals and a low-confidence
///     contribution, but never gates policy on them at threshold. The commercial
///     API-protection feature layers alerting / blocking on top.
/// </summary>
public class IdentityChangeContributor : ConfiguredContributorBase, IFoundationContributor
{
    private readonly ILogger<IdentityChangeContributor> _logger;
    private readonly FingerprintDimSnapshotCache _cache;

    private double CountryChangeWeight => GetParam("country_change_weight", 0.35);
    private double AsnChangeWeight => GetParam("asn_change_weight", 0.20);
    private double UaFamilyChangeWeight => GetParam("ua_family_change_weight", 0.30);
    private double InfraIntroducedWeight => GetParam("infra_introduced_weight", 0.25);
    private double ContributionConfidence => GetParam("contribution_confidence", 0.2);

    public IdentityChangeContributor(
        ILogger<IdentityChangeContributor> logger,
        IDetectorConfigProvider configProvider,
        FingerprintDimSnapshotCache cache) : base(configProvider)
    {
        _logger = logger;
        _cache = cache;
    }

    public override string Name => "IdentityChange";

    // After FingerprintMatch (6), IpContributor (~12), UserAgent (~10), Geo (~20).
    public override int Priority => 30;

    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        new SignalExistsTrigger(SignalKeys.IdentityFingerprintId)
    ];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken cancellationToken = default)
    {
        var fingerprintId = state.GetSignal<string>(SignalKeys.IdentityFingerprintId);
        if (string.IsNullOrEmpty(fingerprintId))
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());

        var current = new FingerprintDimSnapshotCache.DimSnapshot(
            Country: state.GetSignal<string>(SignalKeys.GeoCountryCode) ?? string.Empty,
            Asn: state.GetSignal<string>(SignalKeys.IpAsn) ?? string.Empty,
            UaFamily: state.GetSignal<string>(SignalKeys.UserAgentFamily) ?? string.Empty,
            IsDatacenter: state.GetSignal<bool?>(SignalKeys.IpIsDatacenter) ?? false,
            IsTorOrVpn: (state.GetSignal<bool?>(SignalKeys.GeoIsTor) ?? false)
                     || (state.GetSignal<bool?>(SignalKeys.GeoIsVpn) ?? false)
                     || (state.GetSignal<bool?>(SignalKeys.ThreatIntelTor) ?? false),
            LastSeenUtc: DateTimeOffset.UtcNow);

        var prior = _cache.Get(fingerprintId);

        // First sighting - establish baseline, no risk signals.
        if (prior is null)
        {
            _cache.Set(fingerprintId, current);
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());
        }

        var countryChanged = !string.IsNullOrEmpty(prior.Country)
                          && !string.IsNullOrEmpty(current.Country)
                          && !string.Equals(prior.Country, current.Country, StringComparison.OrdinalIgnoreCase);

        var asnChanged = !string.IsNullOrEmpty(prior.Asn)
                      && !string.IsNullOrEmpty(current.Asn)
                      && !string.Equals(prior.Asn, current.Asn, StringComparison.OrdinalIgnoreCase);

        var uaFamilyChanged = !string.IsNullOrEmpty(prior.UaFamily)
                           && !string.IsNullOrEmpty(current.UaFamily)
                           && !string.Equals(prior.UaFamily, current.UaFamily, StringComparison.OrdinalIgnoreCase);

        // "Introduced" - prior was clean, now we see datacenter / Tor / VPN.
        var infraIntroduced = (!prior.IsDatacenter && current.IsDatacenter)
                           || (!prior.IsTorOrVpn && current.IsTorOrVpn);

        // Refresh the snapshot under "transition becomes baseline" semantics: the
        // alert fires on the transition itself; subsequent requests on the new
        // dims won't refire until they shift again.
        _cache.Set(fingerprintId, current);

        if (!countryChanged && !asnChanged && !uaFamilyChanged && !infraIntroduced)
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());

        var reasonParts = new List<string>(4);
        var score = 0.0;

        if (countryChanged)
        {
            score += CountryChangeWeight;
            reasonParts.Add($"country {prior.Country} -> {current.Country}");
            state.WriteSignal(SignalKeys.RiskCountryChanged, true);
            state.WriteSignal(SignalKeys.RiskCountryTransition, $"{prior.Country} -> {current.Country}");
        }

        if (asnChanged)
        {
            score += AsnChangeWeight;
            reasonParts.Add($"ASN {prior.Asn} -> {current.Asn}");
            state.WriteSignal(SignalKeys.RiskAsnChanged, true);
        }

        if (uaFamilyChanged)
        {
            score += UaFamilyChangeWeight;
            reasonParts.Add($"UA family {prior.UaFamily} -> {current.UaFamily}");
            state.WriteSignal(SignalKeys.RiskUaFamilyChanged, true);
        }

        if (infraIntroduced)
        {
            score += InfraIntroducedWeight;
            reasonParts.Add(current.IsTorOrVpn && !prior.IsTorOrVpn
                ? "Tor/VPN introduced"
                : "datacenter introduced");
            state.WriteSignal(SignalKeys.RiskInfrastructureIntroduced, true);
        }

        // Cap at 1.0 even if every dim shifted at once.
        score = Math.Min(1.0, score);
        var reason = string.Join("; ", reasonParts);

        state.WriteSignal(SignalKeys.RiskSuspiciousChangeScore, score);
        state.WriteSignal(SignalKeys.RiskSuspiciousChangeReason, reason);

        _logger.LogDebug("IdentityChange fp={Fp} score={Score:F2} reason={Reason}",
            fingerprintId.Length > 8 ? fingerprintId[..8] : fingerprintId, score, reason);

        var signals = new Dictionary<string, object>
        {
            [SignalKeys.RiskSuspiciousChangeScore] = score,
            [SignalKeys.RiskSuspiciousChangeReason] = reason
        };

        // Indicator-only contribution: low confidence, scaled by the aggregate score.
        // Commercial layers act on the risk.* signals directly via policy.
        var contribution = BotContribution(
            "SurfaceDimShift",
            $"Matched fingerprint shifted surface dimensions: {reason}",
            confidenceOverride: ContributionConfidence * score,
            botType: BotType.Unknown.ToString());

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(new[]
        {
            contribution with { Signals = signals }
        });
    }
}
