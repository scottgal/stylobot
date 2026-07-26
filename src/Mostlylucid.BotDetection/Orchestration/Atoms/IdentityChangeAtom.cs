using System.Globalization;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;

using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Per-request surface-dimension divergence + stamp for a matched fingerprint (geo
///     country, ASN, UA family, datacenter / Tor introduction, canvas-WebGL shape hash,
///     BotD verdict). Two jobs, neither of which uses a separate store:
/// </summary>
/// <remarks>
///     <para>
///         <b>LIVE divergence (this request scores now).</b> The request's current dims are
///         compared against the fingerprint's ESTABLISHED shape — read at zero cost from the
///         LFU fingerprint entry that is already loaded on every request
///         (<see cref="IFingerprintStore.GetDriftDims"/>). When the current request diverges
///         from the established shape it is suspicious right now (the anti-detect / profile-swap
///         case), so this atom raises the <c>risk.*</c> signals + a low-confidence contribution
///         so the running score reflects it immediately — not only in aggregate after the fact.
///         This is a compute-at-read compare against existing state: no new storage, no
///         per-key eviction, no side cache (the parasitic DimSnapshotCache stays stripped).
///     </para>
///     <para>
///         <b>Stamp for accumulation.</b> The current dims are also stamped as the
///         fingerprint's <c>PendingDims</c> via <see cref="IFingerprintStore.StampObservedDims"/>
///         (no-op if the fingerprint is not resident — never a phantom entry, the #16 leak). The
///         ESTABLISHED baseline is promoted, and the bounded durable drift summary
///         (change frequency + type over time) folded, once at the session → fingerprint
///         ABSORPTION boundary (<see cref="FingerprintAbsorptionService"/>) — not here. This atom
///         never mutates the established baseline; it only reads it for the live compare.
///     </para>
///     <para>
///         The dims ride the single bounded per-fingerprint hot cache in
///         <see cref="IFingerprintStore"/> (co-indexed with the fingerprint entry, co-evicted,
///         never persisted). Priority 30, RequiredSignals(<c>identity.fingerprint_id</c>) so it
///         runs per request once the fingerprint has been resolved.
///     </para>
/// </remarks>
public sealed class IdentityChangeAtom : DetectorAtomBase
{
    private readonly ILogger<IdentityChangeAtom> _logger;
    private readonly IFingerprintStore _store;
    private readonly IDetectorConfigProvider _configProvider;

    public IdentityChangeAtom(
        ILogger<IdentityChangeAtom> logger,
        IDetectorConfigProvider configProvider,
        IFingerprintStore store)
        : base(name: "IdentityChange", category: "IdentityChange")
    {
        _logger = logger;
        _store = store;
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

        var current = new SurfaceDims(
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

        // Read the fingerprint's ESTABLISHED shape from the LFU entry that's already loaded this
        // request (compute-at-read, zero new storage). Then stamp the current dims as PendingDims
        // for the absorption boundary to accumulate. Stamp is a no-op if the fingerprint isn't
        // resident (never a phantom entry — the #16 leak); the baseline is promoted at absorption.
        var (established, _) = _store.GetDriftDims(fingerprintId);
        _store.StampObservedDims(fingerprintId, current);

        // No established baseline yet (first sightings, before the first absorption promotes one)
        // → nothing to diverge from. The stamp above still feeds the accumulation path.
        if (established is null)
            return Task.FromResult(None());

        var prior = established;

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

        // Cap at 1.0 even if every dim diverged at once.
        score = Math.Min(1.0, score);
        var reason = string.Join("; ", reasonParts);

        sink.Raise($"{SignalKeys.RiskSuspiciousChangeScore}:{score.ToString("F3", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.RiskSuspiciousChangeReason}:{reason}", sessionId);

        _logger.LogDebug("IdentityChange live divergence fp={Fp} score={Score:F2} reason={Reason}",
            fingerprintId.Length > 8 ? fingerprintId[..8] : fingerprintId, score, reason);

        return Task.FromResult(Single(new DetectionContribution
        {
            DetectorName = Name,
            Category = "SurfaceDimShift",
            ConfidenceDelta = ContributionConfidence * score,
            Weight = 1.0,
            Reason = $"Request diverges from established fingerprint shape: {reason}",
            BotType = BotType.Unknown.ToString()
        }));
    }

    private static string Truncate(string s) => s.Length > 8 ? s[..8] : s;
}
