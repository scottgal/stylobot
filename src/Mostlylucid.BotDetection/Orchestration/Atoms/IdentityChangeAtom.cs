using System.Globalization;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that flags suspicious shifts in the
///     surface dimensions of a matched fingerprint: geo country, ASN, UA
///     family, and the introduction of datacenter / Tor IP space, shape hash,
///     BotD verdict.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>IdentityChangeContributor</c>. Motivating use case: API-key /
///         cookie theft, where the matched identity is stable but the network
///         and client characteristics around it have shifted in ways very
///         unusual for a single genuine visitor.
///     </para>
///     <para>
///         FOSS stub: writes <c>risk.*</c> signals and a low-confidence
///         contribution but never gates policy on them at threshold. The
///         commercial API-protection feature layers alerting / blocking on top.
///     </para>
///     <para>
///         Cross-request state in the shared
///         <see cref="FingerprintDimSnapshotCache"/>. Priority 30 -- runs after
///         fingerprint match, IP, UA, geo atoms have raised the source
///         dimensions.
///     </para>
/// </remarks>
public sealed class IdentityChangeAtom : DetectorAtomBase
{
    private readonly ILogger<IdentityChangeAtom> _logger;
    private readonly FingerprintDimSnapshotCache _cache;
    private readonly IDetectorConfigProvider _configProvider;

    public IdentityChangeAtom(
        ILogger<IdentityChangeAtom> logger,
        IDetectorConfigProvider configProvider,
        FingerprintDimSnapshotCache cache)
        : base(name: "IdentityChange", category: "IdentityChange")
    {
        _logger = logger;
        _cache = cache;
        _configProvider = configProvider;
    }

    public override int Priority => 30;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.IdentityFingerprintId };

    private double CountryChangeWeight => _configProvider.GetParameter(Name, "country_change_weight", 0.35);
    private double AsnChangeWeight => _configProvider.GetParameter(Name, "asn_change_weight", 0.20);
    private double UaFamilyChangeWeight => _configProvider.GetParameter(Name, "ua_family_change_weight", 0.30);
    private double InfraIntroducedWeight => _configProvider.GetParameter(Name, "infra_introduced_weight", 0.25);
    private double ShapeHashChangeWeight => _configProvider.GetParameter(Name, "shape_hash_change_weight", 0.40);
    private double BotdKindChangeWeight => _configProvider.GetParameter(Name, "botd_kind_change_weight", 0.20);
    private double ContributionConfidence => _configProvider.GetParameter(Name, "contribution_confidence", 0.2);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var fingerprintId = sink.ReadHint(SignalKeys.IdentityFingerprintId);
        if (string.IsNullOrEmpty(fingerprintId))
            return Task.FromResult(None());

        var current = new FingerprintDimSnapshotCache.DimSnapshot(
            Country: sink.ReadHint(SignalKeys.GeoCountryCode) ?? string.Empty,
            Asn: sink.ReadHint(SignalKeys.IpAsn) ?? string.Empty,
            UaFamily: sink.ReadHint(SignalKeys.UserAgentFamily) ?? string.Empty,
            IsDatacenter: sink.ReadBoolHint(SignalKeys.IpIsDatacenter),
            IsTorOrVpn: sink.ReadBoolHint(SignalKeys.GeoIsTor)
                     || sink.ReadBoolHint(SignalKeys.GeoIsVpn)
                     || sink.ReadBoolHint(SignalKeys.ThreatIntelTor),
            LastSeenUtc: DateTimeOffset.UtcNow,
            ShapeHash: sink.ReadHint(SignalKeys.ClientSideShapeHash) ?? string.Empty,
            BotdKind: sink.ReadHint(SignalKeys.ClientSideBotdKind) ?? string.Empty);

        var prior = _cache.Get(fingerprintId);

        // First sighting -- establish baseline, no risk signals.
        if (prior is null)
        {
            _cache.Set(fingerprintId, current);
            return Task.FromResult(None());
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

        var infraIntroduced = (!prior.IsDatacenter && current.IsDatacenter)
                           || (!prior.IsTorOrVpn && current.IsTorOrVpn);

        var shapeHashChanged = !string.IsNullOrEmpty(prior.ShapeHash)
                            && !string.IsNullOrEmpty(current.ShapeHash)
                            && !string.Equals(prior.ShapeHash, current.ShapeHash, StringComparison.Ordinal);

        var botdKindChanged = !string.IsNullOrEmpty(prior.BotdKind)
                           && !string.IsNullOrEmpty(current.BotdKind)
                           && !string.Equals(prior.BotdKind, current.BotdKind, StringComparison.OrdinalIgnoreCase);

        // Refresh the snapshot under "transition becomes baseline" semantics
        _cache.Set(fingerprintId, current);

        if (!countryChanged && !asnChanged && !uaFamilyChanged && !infraIntroduced
            && !shapeHashChanged && !botdKindChanged)
            return Task.FromResult(None());

        var reasonParts = new List<string>(4);
        var score = 0.0;

        if (countryChanged)
        {
            score += CountryChangeWeight;
            reasonParts.Add($"country {prior.Country} -> {current.Country}");
            sink.Raise(SignalKeys.RiskCountryChanged, sessionId);
            sink.Raise($"{SignalKeys.RiskCountryTransition}:{prior.Country} -> {current.Country}", sessionId);
        }

        if (asnChanged)
        {
            score += AsnChangeWeight;
            reasonParts.Add($"ASN {prior.Asn} -> {current.Asn}");
            sink.Raise(SignalKeys.RiskAsnChanged, sessionId);
        }

        if (uaFamilyChanged)
        {
            score += UaFamilyChangeWeight;
            reasonParts.Add($"UA family {prior.UaFamily} -> {current.UaFamily}");
            sink.Raise(SignalKeys.RiskUaFamilyChanged, sessionId);
        }

        if (infraIntroduced)
        {
            score += InfraIntroducedWeight;
            reasonParts.Add(current.IsTorOrVpn && !prior.IsTorOrVpn
                ? "Tor/VPN introduced"
                : "datacenter introduced");
            sink.Raise(SignalKeys.RiskInfrastructureIntroduced, sessionId);
        }

        if (shapeHashChanged)
        {
            score += ShapeHashChangeWeight;
            reasonParts.Add($"shape hash {Truncate(prior.ShapeHash)} -> {Truncate(current.ShapeHash)}");
            sink.Raise(SignalKeys.RiskShapeHashChanged, sessionId);
        }

        if (botdKindChanged)
        {
            score += BotdKindChangeWeight;
            reasonParts.Add($"BotD kind {prior.BotdKind} -> {current.BotdKind}");
            sink.Raise(SignalKeys.RiskBotdKindChanged, sessionId);
        }

        // Cap at 1.0 even if every dim shifted at once.
        score = Math.Min(1.0, score);
        var reason = string.Join("; ", reasonParts);

        sink.Raise($"{SignalKeys.RiskSuspiciousChangeScore}:{score.ToString("F3", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.RiskSuspiciousChangeReason}:{reason}", sessionId);

        _logger.LogDebug("IdentityChange fp={Fp} score={Score:F2} reason={Reason}",
            fingerprintId.Length > 8 ? fingerprintId[..8] : fingerprintId, score, reason);

        return Task.FromResult(Single(new DetectionContribution
        {
            DetectorName = Name,
            Category = "SurfaceDimShift",
            ConfidenceDelta = ContributionConfidence * score,
            Weight = 1.0,
            Reason = $"Matched fingerprint shifted surface dimensions: {reason}",
            BotType = BotType.Unknown.ToString()
        }));
    }

    private static string Truncate(string s) => s.Length > 8 ? s[..8] : s;
}
