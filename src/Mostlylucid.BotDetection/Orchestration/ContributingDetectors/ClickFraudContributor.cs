using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Click fraud and invalid ad traffic (IVT) detection contributor.
///     Reads UTM/click-ID signals written by <see cref="PiiQueryStringContributor" /> and
///     combines them with IP, geo, fingerprint, session, and transport signals to score
///     the likelihood that a paid-ad click originated from automated or fraudulent traffic.
///
///     IAB-IVT classification: SIVT (Sophisticated Invalid Traffic).
///     Priority 38 - runs after session/IP/geo enrichment but before IntentContributor (40)
///     so that downstream detectors can read clickfraud.* signals.
///
///     Configuration loaded from: clickfraud.detector.yaml
///     Override via: appsettings.json -> BotDetection:Detectors:ClickFraudContributor:*
/// </summary>
public class ClickFraudContributor : ConfiguredContributorBase
{
    private readonly ILogger<ClickFraudContributor> _logger;

    public ClickFraudContributor(
        ILogger<ClickFraudContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
    }

    public override string Name => "ClickFraud";
    public override int Priority => Manifest?.Priority ?? 38;

    /// <summary>
    ///     Trigger when utm.present is on the blackboard (paid traffic landed),
    ///     OR when both session.request_count and ip.is_datacenter are available
    ///     (organic datacenter traffic is also worth scoring).
    /// </summary>
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        Triggers.AnyOf(
            Triggers.WhenSignalExists(SignalKeys.UtmPresent),
            Triggers.AllOf(
                Triggers.WhenSignalExists(SignalKeys.SessionRequestCount),
                Triggers.WhenSignalExists(SignalKeys.IpIsDatacenter)))
    ];

    // YAML-configurable weights - no magic numbers in code
    private double DatacenterPaidWeight => GetParam("datacenter_paid_weight", 0.50);
    private double DatacenterUnpaidWeight => GetParam("datacenter_unpaid_weight", 0.15);
    private double VpnPaidWeight => GetParam("vpn_paid_weight", 0.25);
    private double ProxyPaidWeight => GetParam("proxy_paid_weight", 0.20);
    private double ReferrerMismatchClickIdWeight => GetParam("referrer_mismatch_clickid_weight", 0.40);
    private double ReferrerMismatchPaidWeight => GetParam("referrer_mismatch_paid_weight", 0.25);
    private double SinglePageWeight => GetParam("single_page_weight", 0.20);
    private double NoAssetsWeight => GetParam("no_assets_weight", 0.15);
    private double HeadlessPaidWeight => GetParam("headless_paid_weight", 0.40);
    private double HeadlessUnpaidWeight => GetParam("headless_unpaid_weight", 0.20);
    private double BotThreshold => GetParam("bot_threshold", 0.55);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        // Gate signal: always write so downstream detectors know we ran
        state.WriteSignal(SignalKeys.ClickFraudChecked, true);

        // Read signals set by earlier contributors
        var utmPresent = state.GetSignal<bool>(SignalKeys.UtmPresent);
        var hasGclid = state.GetSignal<bool>(SignalKeys.UtmHasGclid);
        var hasFbclid = state.GetSignal<bool>(SignalKeys.UtmHasFbclid);
        var hasMsclkid = state.GetSignal<bool>(SignalKeys.UtmHasMsclkid);
        var hasTtclid = state.GetSignal<bool>(SignalKeys.UtmHasTtclid);
        var referrerMismatch = state.GetSignal<bool>(SignalKeys.UtmReferrerMismatch);
        var isDatacenter = state.GetSignal<bool>(SignalKeys.IpIsDatacenter);
        var isVpn = state.GetSignal<bool>(SignalKeys.GeoIsVpn);
        var isProxy = state.GetSignal<bool>(SignalKeys.GeoIsProxy);
        var headlessScore = state.GetSignal<double>(SignalKeys.FingerprintHeadlessScore);
        var assetCount = state.GetSignal<int>(SignalKeys.ResourceAssetCount);
        var sessionRequestCount = state.GetSignal<int>(SignalKeys.SessionRequestCount);
        var protocolClass = state.GetSignal<string>(SignalKeys.TransportProtocolClass);

        // Derived booleans
        var hasClickId = hasGclid || hasFbclid || hasMsclkid || hasTtclid;
        var isPaidTraffic = utmPresent || hasClickId;
        var isHeadless = headlessScore > 0.5;

        state.WriteSignal(SignalKeys.ClickFraudIsPaidTraffic, isPaidTraffic);

        // Score accumulation - each check adds a weighted contribution
        var score = 0.0;
        var pattern = string.Empty;
        var reasons = new List<string>(8);

        // 1. Datacenter IP
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

        // 2. VPN on paid traffic
        if (isVpn && isPaidTraffic)
        {
            score += VpnPaidWeight;
            if (string.IsNullOrEmpty(pattern)) pattern = "vpn_paid";
            reasons.Add("VPN/anonymizer on paid-ad landing");
        }

        // 3. Proxy on paid traffic
        if (isProxy && isPaidTraffic)
        {
            score += ProxyPaidWeight;
            if (string.IsNullOrEmpty(pattern)) pattern = "proxy_paid";
            reasons.Add("open proxy on paid-ad landing");
        }

        // 4. Referrer mismatch
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

        // 5. Single-page session (immediate bounce)
        if (sessionRequestCount == 1)
        {
            score += SinglePageWeight;
            if (string.IsNullOrEmpty(pattern)) pattern = "immediate_bounce";
            reasons.Add("single-page session (immediate bounce)");
        }

        // 6. No assets loaded on a document request (engagement void)
        if (assetCount == 0 && string.Equals(protocolClass, "document", StringComparison.OrdinalIgnoreCase))
        {
            score += NoAssetsWeight;
            if (string.IsNullOrEmpty(pattern)) pattern = "engagement_void";
            reasons.Add("document request with zero assets loaded");
        }

        // 7. Headless browser
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

        // Cap score at 1.0
        score = Math.Min(score, 1.0);

        // Write output signals
        state.WriteSignal(SignalKeys.ClickFraudConfidence, score);
        if (!string.IsNullOrEmpty(pattern))
            state.WriteSignal(SignalKeys.ClickFraudPattern, pattern);

        // Decide contribution based on threshold
        if (score < BotThreshold)
        {
            var message = score > 0.0
                ? $"Click fraud score {score:F2} below threshold {BotThreshold:F2} - pattern: {(string.IsNullOrEmpty(pattern) ? "none" : pattern)}"
                : "No click fraud signals detected";

            return Task.FromResult(Single(NeutralContribution("ClickFraud", message)));
        }

        var reasonSummary = string.Join("; ", reasons);
        var contribution = BotContribution(
            "ClickFraud",
            $"Click fraud detected (score={score:F2}, pattern={pattern}): {reasonSummary}",
            confidenceOverride: score,
            botType: BotType.ClickFraud.ToString());

        _logger.LogDebug(
            "ClickFraudContributor: score={Score:F2} pattern={Pattern} reasons={Reasons}",
            score, pattern, reasonSummary);

        return Task.FromResult(Single(contribution));
    }
}
