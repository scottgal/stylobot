using System.Globalization;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that scores the likelihood a
///     paid-ad click originated from automated / fraudulent traffic. IAB-IVT
///     classification: SIVT (Sophisticated Invalid Traffic).
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>ClickFraudContributor</c>. Priority 38 -- after session / IP /
///         geo enrichment but before the intent atom (40) so downstream
///         detectors can read <c>clickfraud.*</c> signals.
///     </para>
///     <para>
///         Pure signal-reader: composes seven weighted checks (datacenter,
///         VPN, proxy, referrer mismatch, single-page bounce, engagement
///         void, headless) into a single fraud confidence + pattern label.
///         All inputs come off sink hints; no HttpContext needed.
///     </para>
///     <para>
///         Trigger: <see cref="SignalKeys.UtmPresent"/> OR
///         (<see cref="SignalKeys.SessionRequestCount"/> AND
///         <see cref="SignalKeys.IpIsDatacenter"/>) -- the atom's
///         <see cref="RequiredSignals"/> encodes the intersection portion;
///         the OR arm is checked inline (UTM presence bypasses the require).
///     </para>
/// </remarks>
public sealed class ClickFraudAtom : DetectorAtomBase
{
    private readonly ILogger<ClickFraudAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;

    public ClickFraudAtom(
        ILogger<ClickFraudAtom> logger,
        IDetectorConfigProvider configProvider)
        : base(name: "ClickFraud", category: "ClickFraud")
    {
        _logger = logger;
        _configProvider = configProvider;
    }

    public override int Priority => 38;

    // The legacy TriggerCondition is an OR: UTM present, OR both session+datacenter.
    // A single-key RequiredSignals filter would drop half the coverage. Leave
    // RequiredSignals empty and gate inside DetectAsync so both arms are honoured.
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double DatacenterPaidWeight => _configProvider.GetParameter(Name, "datacenter_paid_weight", 0.50);
    private double DatacenterUnpaidWeight => _configProvider.GetParameter(Name, "datacenter_unpaid_weight", 0.15);
    private double VpnPaidWeight => _configProvider.GetParameter(Name, "vpn_paid_weight", 0.25);
    private double ProxyPaidWeight => _configProvider.GetParameter(Name, "proxy_paid_weight", 0.20);
    private double ReferrerMismatchClickIdWeight => _configProvider.GetParameter(Name, "referrer_mismatch_clickid_weight", 0.40);
    private double ReferrerMismatchPaidWeight => _configProvider.GetParameter(Name, "referrer_mismatch_paid_weight", 0.25);
    private double SinglePageWeight => _configProvider.GetParameter(Name, "single_page_weight", 0.20);
    private double NoAssetsWeight => _configProvider.GetParameter(Name, "no_assets_weight", 0.15);
    private double HeadlessPaidWeight => _configProvider.GetParameter(Name, "headless_paid_weight", 0.40);
    private double HeadlessUnpaidWeight => _configProvider.GetParameter(Name, "headless_unpaid_weight", 0.20);
    private double BotThreshold => _configProvider.GetParameter(Name, "bot_threshold", 0.55);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {

        // OR-trigger evaluation -- UTM present is the paid-traffic arm; the
        // organic-datacenter arm needs both session count and datacenter.
        var utmPresent = sink.ReadBoolHint(SignalKeys.UtmPresent);
        var hasSessionCount = sink.ReadHint(SignalKeys.SessionRequestCount) is not null;
        var hasDatacenterHint = sink.ReadHint(SignalKeys.IpIsDatacenter) is not null;
        if (!utmPresent && !(hasSessionCount && hasDatacenterHint))
            return Task.FromResult(None());

        sink.Raise($"{SignalKeys.ClickFraudChecked}:true", sessionId);

        var hasGclid = sink.ReadBoolHint(SignalKeys.UtmHasGclid);
        var hasFbclid = sink.ReadBoolHint(SignalKeys.UtmHasFbclid);
        var hasMsclkid = sink.ReadBoolHint(SignalKeys.UtmHasMsclkid);
        var hasTtclid = sink.ReadBoolHint(SignalKeys.UtmHasTtclid);
        var referrerMismatch = sink.ReadBoolHint(SignalKeys.UtmReferrerMismatch);
        var isDatacenter = sink.ReadBoolHint(SignalKeys.IpIsDatacenter);
        var isVpn = sink.ReadBoolHint(SignalKeys.GeoIsVpn);
        var isProxy = sink.ReadBoolHint(SignalKeys.GeoIsProxy);
        var headlessScore = sink.ReadDoubleHint(SignalKeys.FingerprintHeadlessScore);
        var assetCount = sink.ReadIntHint(SignalKeys.ResourceAssetCount);
        var sessionRequestCount = sink.ReadIntHint(SignalKeys.SessionRequestCount);
        var protocolClass = sink.ReadHint(SignalKeys.TransportProtocolClass);

        var hasClickId = hasGclid || hasFbclid || hasMsclkid || hasTtclid;
        var isPaidTraffic = utmPresent || hasClickId;
        var isHeadless = headlessScore > 0.5;

        sink.Raise($"{SignalKeys.ClickFraudIsPaidTraffic}:{(isPaidTraffic ? "true" : "false")}", sessionId);

        var score = 0.0;
        var pattern = string.Empty;
        var reasons = new List<string>(8);

        if (isDatacenter)
        {
            if (isPaidTraffic)
            {
                score += DatacenterPaidWeight;
                pattern = "datacenter_paid";
                reasons.Add("datacenter IP on paid-ad landing");
            }
            else
            {
                score += DatacenterUnpaidWeight;
                if (string.IsNullOrEmpty(pattern)) pattern = "organic_datacenter";
                reasons.Add("datacenter IP on organic request");
            }
        }

        if (isVpn && isPaidTraffic)
        {
            score += VpnPaidWeight;
            if (string.IsNullOrEmpty(pattern)) pattern = "vpn_paid";
            reasons.Add("VPN/anonymizer on paid-ad landing");
        }

        if (isProxy && isPaidTraffic)
        {
            score += ProxyPaidWeight;
            if (string.IsNullOrEmpty(pattern)) pattern = "proxy_paid";
            reasons.Add("open proxy on paid-ad landing");
        }

        if (referrerMismatch)
        {
            if (hasClickId)
            {
                score += ReferrerMismatchClickIdWeight;
                if (string.IsNullOrEmpty(pattern)) pattern = "referrer_spoof";
                reasons.Add("referrer mismatch with click-ID present (referrer spoofing)");
            }
            else if (isPaidTraffic)
            {
                score += ReferrerMismatchPaidWeight;
                if (string.IsNullOrEmpty(pattern)) pattern = "referrer_spoof";
                reasons.Add("referrer mismatch on paid-ad landing");
            }
        }

        if (sessionRequestCount == 1)
        {
            score += SinglePageWeight;
            if (string.IsNullOrEmpty(pattern)) pattern = "immediate_bounce";
            reasons.Add("single-page session (immediate bounce)");
        }

        if (assetCount == 0 && string.Equals(protocolClass, "document", StringComparison.OrdinalIgnoreCase))
        {
            score += NoAssetsWeight;
            if (string.IsNullOrEmpty(pattern)) pattern = "engagement_void";
            reasons.Add("document request with zero assets loaded");
        }

        if (isHeadless)
        {
            if (isPaidTraffic)
            {
                score += HeadlessPaidWeight;
                if (string.IsNullOrEmpty(pattern)) pattern = "headless_paid";
                reasons.Add($"headless browser on paid-ad landing (headless_score={headlessScore:F2})");
            }
            else
            {
                score += HeadlessUnpaidWeight;
                if (string.IsNullOrEmpty(pattern)) pattern = "headless_organic";
                reasons.Add($"headless browser (headless_score={headlessScore:F2})");
            }
        }

        score = Math.Min(score, 1.0);

        sink.Raise($"{SignalKeys.ClickFraudConfidence}:{score.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        if (!string.IsNullOrEmpty(pattern))
            sink.Raise($"{SignalKeys.ClickFraudPattern}:{pattern}", sessionId);

        if (score < BotThreshold)
        {
            var message = score > 0.0
                ? $"Click fraud score {score:F2} below threshold {BotThreshold:F2} - pattern: {(string.IsNullOrEmpty(pattern) ? "none" : pattern)}"
                : "No click fraud signals detected";
            return Task.FromResult(Single(DetectionContribution.Info(Name, Category, message)));
        }

        var reasonSummary = string.Join("; ", reasons);
        var contribution = new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = score,
            Weight = 1.0,
            Reason = $"Click fraud detected (score={score:F2}, pattern={pattern}): {reasonSummary}",
            BotType = BotType.ClickFraud.ToString()
        };

        _logger.LogDebug(
            "ClickFraudAtom: score={Score:F2} pattern={Pattern} reasons={Reasons}",
            score, pattern, reasonSummary);

        return Task.FromResult(Single(contribution));
    }
}
