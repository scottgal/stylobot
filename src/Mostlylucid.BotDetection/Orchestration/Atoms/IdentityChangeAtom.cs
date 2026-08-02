using System.Globalization;
using Microsoft.AspNetCore.Http;
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
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IdentityGlobalWeightsCache _globalWeights;

    public IdentityChangeAtom(
        ILogger<IdentityChangeAtom> logger,
        IDetectorConfigProvider configProvider,
        IFingerprintStore store,
        IHttpContextAccessor httpContextAccessor,
        IdentityGlobalWeightsCache globalWeights)
        : base(name: "IdentityChange", category: "IdentityChange")
    {
        _logger = logger;
        _store = store;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _globalWeights = globalWeights;
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

    // Accumulated durable drift frequency (#16 wired): a fingerprint whose surface dims
    // change OFTEN over time is the anti-detect / profile-cycling browser signature, so
    // its CURRENT request scores suspicious independent of whether THIS request diverged.
    private double DriftFrequencyHighThreshold => _configProvider.GetParameter(Name, "drift_frequency_high_threshold", 0.3);
    private double DriftFrequencyWeight => _configProvider.GetParameter(Name, "drift_frequency_weight", 0.3);

    // Behavioural shape drift (LIVE, per-request): weighted-cosine between THIS request's own
    // identity vector and the fingerprint's established centroid+weights. Same warning
    // threshold FingerprintDriftService's background audit pass uses, kept as an independent
    // atom-owned parameter so live scoring and the background badge can be tuned separately.
    private double BehavioralDriftWarningThreshold => _configProvider.GetParameter(Name, "behavioral_drift_warning_threshold", 0.92);
    private double BehavioralDriftWeight => _configProvider.GetParameter(Name, "behavioral_drift_weight", 0.3);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var fingerprintId = sink.ReadHint(SignalKeys.IdentityFingerprintId);
        if (string.IsNullOrEmpty(fingerprintId))
            return None();

        var contributions = new List<DetectionContribution>(2);

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

        // LIVE divergence — this request scores NOW when it differs from the ESTABLISHED shape.
        // Skipped (no contribution) when there is no baseline yet (first sightings, before the
        // first absorption promotes one) or when nothing diverged; the stamp above still feeds
        // the accumulation path either way. When it fires, its contribution is ADDED to the list
        // alongside any drift-frequency contribution below (both can fire on the same request).
        if (established is not null)
        {
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

            if (countryChanged || asnChanged || uaFamilyChanged || infraIntroduced
                || shapeHashChanged || botdKindChanged)
            {
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

                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "SurfaceDimShift",
                    ConfidenceDelta = ContributionConfidence * score,
                    Weight = 1.0,
                    Reason = $"Request diverges from established fingerprint shape: {reason}",
                    BotType = BotType.Unknown.ToString()
                });
            }
        }

        // Accumulated durable drift frequency (#16 wired as a LIVE input). The matched
        // fingerprint is resident (RequiredSignals(identity.fingerprint_id) guarantees it was
        // matched), so this is a cache hit — no DB round-trip. A fingerprint whose surface dims
        // change OFTEN over time is the anti-detect / profile-cycling browser signature, so its
        // current request scores suspicious even if THIS request itself did not diverge.
        var fp = await _store.GetFingerprintAsync(fingerprintId, ct);
        if (fp is not null && fp.DriftFrequency >= DriftFrequencyHighThreshold)
        {
            sink.Raise(SignalKeys.RiskDriftFrequencyHigh, sessionId);
            sink.Raise($"{SignalKeys.RiskDriftFrequency}:{fp.DriftFrequency.ToString("F3", CultureInfo.InvariantCulture)}", sessionId);

            // Confidence scales with the accumulated frequency, clamped to [0, DriftFrequencyWeight].
            var driftDelta = Math.Clamp(DriftFrequencyWeight * fp.DriftFrequency, 0.0, DriftFrequencyWeight);

            _logger.LogDebug("IdentityChange drift-frequency fp={Fp} freq={Freq:F2} delta={Delta:F2}",
                fingerprintId.Length > 8 ? fingerprintId[..8] : fingerprintId, fp.DriftFrequency, driftDelta);

            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "DriftFrequency",
                ConfidenceDelta = driftDelta,
                Weight = 1.0,
                Reason = $"Fingerprint drifts frequently (freq {fp.DriftFrequency:F2}) — anti-detect / profile-cycling pattern",
                BotType = BotType.Unknown.ToString()
            });
        }

        // Behavioural shape drift (LIVE, per-request). Distinct from the surface-dims compare
        // above (geo/ASN/UA/canvas): this is the continuous behavioural/header SHAPE, so it
        // catches drift even when every discrete surface dim stays put -- the "Adblocker ->
        // curl" case (same IP/UA/geo, different tool fingerprint). Mirrors
        // FingerprintDriftService's background weighted-cosine check, but computed inline
        // against THIS request's own IdentityVectorAtom-composed vector rather than a stored
        // "latest observation", so it feeds scoring in real time instead of only the
        // background-tick audit badge. Gated on CentroidMaturity > 0 -- a cold-start
        // fingerprint's centroid is all-zero, so comparing against it would be a meaningless,
        // guaranteed-below-threshold false positive on every brand-new visitor.
        if (fp is not null && fp.CentroidMaturity > 0
            && _httpContextAccessor.HttpContext is { } context
            && IdentityVectorAtom.TryGetVector(context) is { } currentVector
            && currentVector.Length == fp.Centroid.Length)
        {
            var composedWeights = _globalWeights.Compose(fp.Weights);
            var similarity = BruteForceIdentityAnchorIndex.WeightedCosine(currentVector, fp.Centroid, composedWeights);

            if (similarity < BehavioralDriftWarningThreshold)
            {
                sink.Raise(SignalKeys.RiskBehavioralDriftHigh, sessionId);
                sink.Raise($"{SignalKeys.RiskBehavioralDriftScore}:{similarity.ToString("F3", CultureInfo.InvariantCulture)}", sessionId);

                var deficit = Math.Clamp(BehavioralDriftWarningThreshold - similarity, 0.0, 1.0);
                var driftDelta = Math.Clamp(BehavioralDriftWeight * deficit, 0.0, BehavioralDriftWeight);

                _logger.LogDebug("IdentityChange behavioral-drift fp={Fp} similarity={Similarity:F2} delta={Delta:F2}",
                    fingerprintId.Length > 8 ? fingerprintId[..8] : fingerprintId, similarity, driftDelta);

                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "BehavioralDrift",
                    ConfidenceDelta = driftDelta,
                    Weight = 1.0,
                    Reason = $"Request's behavioural shape diverges from established fingerprint centroid (similarity {similarity:F2})",
                    BotType = BotType.Unknown.ToString()
                });
            }
        }

        return contributions.Count > 0 ? contributions : None();
    }

    private static string Truncate(string s) => s.Length > 8 ? s[..8] : s;
}
