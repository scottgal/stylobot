using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
/// Tests whether observed request behavior is consistent with the UA family claimed in the User-Agent header.
/// <para>
/// A UA string is a testable claim. Chrome says it is Chrome: it should send Sec-Fetch headers,
/// Accept-Language, and request from a residential IP. A bot claiming to be Chrome will score low
/// against Chrome's behavioral centroid.
/// </para>
/// <para>
/// Centroids are seeded from ua_profiles.yaml and auto-adapt via EWM (alpha=0.99).
/// Restarting resets to seeds by design - the YAML values are "likely human" starting shapes, not commandments.
/// Reactive signals (Retry-After compliance, backoff pattern) are centroid dimensions so 429-probing
/// is naturally part of claimed-identity consistency.
/// </para>
/// <para>Signals emitted:</para>
/// <list type="bullet">
/// <item><c>claimed_identity.family</c> - canonical UA family name</item>
/// <item><c>claimed_identity.tier</c> - profile tier (browser, crawler, tool, reader)</item>
/// <item><c>claimed_identity.consistency_score</c> - [0,1] match to profile centroid</item>
/// <item><c>claimed_identity.has_profile</c> - false when no seed profile exists for this family</item>
/// </list>
/// </summary>
public sealed class ClaimedIdentityContributor : ConfiguredContributorBase
{
    private readonly ILogger<ClaimedIdentityContributor> _logger;
    private readonly UaProfileStore _profileStore;

    private double ConsistencyThreshold => GetParam("consistency_threshold", 0.55);
    private double BotSignalConfidence => GetParam("bot_signal_confidence", 0.35);
    private double UpdateAlpha => GetParam("update_alpha", 0.99);

    public ClaimedIdentityContributor(
        ILogger<ClaimedIdentityContributor> logger,
        IDetectorConfigProvider configProvider,
        UaProfileStore profileStore) : base(configProvider)
    {
        _logger = logger;
        _profileStore = profileStore;
    }

    public override string Name => "ClaimedIdentity";
    public override int Priority => 35; // After ReactivePattern (32)

    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        new SignalExistsTrigger(SignalKeys.UserAgentFamily)
    ];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        var rawFamily = state.GetSignal<string>(SignalKeys.UserAgentFamily);

        // Skip known bots - UserAgentContributor already handles them
        var uaIsBot = state.GetSignal<bool>(SignalKeys.UserAgentIsBot);
        if (uaIsBot)
        {
            state.WriteSignal(SignalKeys.ClaimedIdentityHasProfile, false);
            contributions.Add(NeutralContribution("ClaimedIdentity", "UA already identified as known bot"));
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
        }

        var centroid = _profileStore.GetCentroid(rawFamily);
        if (centroid == null)
        {
            state.WriteSignal(SignalKeys.ClaimedIdentityHasProfile, false);
            contributions.Add(NeutralContribution("ClaimedIdentity", $"No profile for UA family '{rawFamily}'"));
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
        }

        state.WriteSignal(SignalKeys.ClaimedIdentityHasProfile, true);
        state.WriteSignal(SignalKeys.ClaimedIdentityFamily, centroid.Family);
        state.WriteSignal(SignalKeys.ClaimedIdentityTier, centroid.Tier);

        var observed = ObserveDimensions(state);
        var consistencyScore = ComputeConsistency(observed, centroid);

        state.WriteSignal(SignalKeys.ClaimedIdentityConsistencyScore, consistencyScore);

        var signature = state.GetSignal<string>(SignalKeys.PrimarySignature) ?? "";
        _profileStore.RecordSignature(signature, centroid.Family, centroid.Tier, consistencyScore);

        // Auto-update centroid if request looks genuine (consistency high + not from DC for browser tier)
        if (ShouldUpdateCentroid(centroid, consistencyScore, state))
            _profileStore.UpdateCentroid(rawFamily!, observed);

        if (consistencyScore < ConsistencyThreshold)
        {
            var reason = DescribeInconsistency(observed, centroid, consistencyScore);
            _logger.LogDebug(
                "ClaimedIdentity: {Family} consistency={Score:F2} < threshold={Threshold:F2}: {Reason}",
                centroid.Family, consistencyScore, ConsistencyThreshold, reason);
            contributions.Add(BotContribution(
                "ClaimedIdentity",
                $"{centroid.Family} behavioral profile mismatch (consistency={consistencyScore:F2}): {reason}",
                BotSignalConfidence));
        }
        else
        {
            contributions.Add(HumanContribution(
                "ClaimedIdentity",
                $"{centroid.Family} behavioral profile consistent (score={consistencyScore:F2})",
                0.15));
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private Dictionary<string, float> ObserveDimensions(BlackboardState state)
    {
        var request = state.HttpContext?.Request;
        var dims = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        // sec_fetch_present: W3C Sec-Fetch-Mode or Sec-Fetch-Site header present
        var secFetchMode = state.GetSignal<string>(SignalKeys.HeaderSecFetchMode);
        dims["sec_fetch_present"] = string.IsNullOrEmpty(secFetchMode) ? 0f : 1f;

        // accept_html: Accept header includes text/html
        var accept = request?.Headers["Accept"].ToString() ?? "";
        dims["accept_html"] = accept.Contains("text/html", StringComparison.OrdinalIgnoreCase) ? 1f : 0f;

        // accept_language: Accept-Language header present
        dims["accept_language"] = request?.Headers.ContainsKey("Accept-Language") == true ? 1f : 0f;

        // accept_encoding: Accept-Encoding header present
        dims["accept_encoding"] = request?.Headers.ContainsKey("Accept-Encoding") == true ? 1f : 0f;

        // datacenter_ip: IP is a datacenter/VPN address
        var isDc = state.GetSignal<bool>(SignalKeys.IpIsDatacenter);
        dims["datacenter_ip"] = isDc ? 1f : 0f;

        // Reactive dimensions: only include if enough error events
        var errorCount = state.GetSignal<int>(SignalKeys.ReactiveErrorEventCount);
        if (errorCount >= 2)
        {
            var compliance = state.GetSignal<float>(SignalKeys.ReactiveRetryAfterCompliance);
            if (compliance > 0) // -1 = no Retry-After header seen
            {
                // Near 1.0 = mechanical precision (bot-like). Distance from 1.0 = human-like.
                var precision = (float)Math.Max(0.0, 1.0 - Math.Abs(compliance - 1.0));
                dims["reactive_precision"] = precision;
            }

            var backoffPattern = state.GetSignal<string>(SignalKeys.ReactiveBackoffPattern) ?? "none";
            dims["reactive_mechanical"] = backoffPattern is "exponential" or "fibonacci" or "linear" ? 1f : 0f;
        }

        return dims;
    }

    private static double ComputeConsistency(Dictionary<string, float> observed, LiveCentroid centroid)
    {
        var totalWeight = 0.0;
        var totalScore = 0.0;

        foreach (var (dim, liveDim) in centroid.Dimensions)
        {
            if (!observed.TryGetValue(dim, out var obs))
                continue; // dimension not observed for this request - skip

            var similarity = 1.0 - Math.Abs(obs - liveDim.Mean);
            totalWeight += liveDim.Weight;
            totalScore += liveDim.Weight * similarity;
        }

        return totalWeight > 0 ? totalScore / totalWeight : 0.5;
    }

    private static bool ShouldUpdateCentroid(LiveCentroid centroid, double consistencyScore, BlackboardState state)
    {
        // Only update if request looks like genuine traffic from this UA family.
        // Prevents bots from poisoning the centroid.
        if (consistencyScore < 0.65) return false;

        // For browser-tier profiles: DC IPs are suspicious - don't update with them
        if (centroid.Tier == "browser")
        {
            var isDc = state.GetSignal<bool>(SignalKeys.IpIsDatacenter);
            if (isDc) return false;
        }

        return true;
    }

    private static string DescribeInconsistency(Dictionary<string, float> observed, LiveCentroid centroid, double score)
    {
        var violations = new List<string>(4);
        foreach (var (dim, liveDim) in centroid.Dimensions.OrderByDescending(d => d.Value.Weight))
        {
            if (!observed.TryGetValue(dim, out var obs)) continue;
            var deviation = Math.Abs(obs - liveDim.Mean);
            if (deviation > 0.4) // notable discrepancy
                violations.Add(FormatViolation(dim, obs, liveDim.Mean));
            if (violations.Count >= 3) break;
        }

        return violations.Count > 0
            ? string.Join("; ", violations)
            : $"overall score {score:F2}";
    }

    private static string FormatViolation(string dim, float observed, double expected) => dim switch
    {
        "sec_fetch_present" => observed < 0.5
            ? "missing Sec-Fetch headers (expected for this browser)"
            : "unexpected Sec-Fetch headers (not expected for this client)",
        "accept_html" => observed < 0.5
            ? "no text/html in Accept (expected for browser)"
            : "text/html in Accept (unexpected for this client type)",
        "accept_language" => observed < 0.5
            ? "no Accept-Language (browsers always send this)"
            : "unexpected Accept-Language",
        "datacenter_ip" => observed > 0.5
            ? "browser UA from datacenter IP"
            : "crawler UA from residential IP (possibly using proxy)",
        "reactive_precision" => observed > 0.7
            ? "mechanical Retry-After compliance (inhuman timing)"
            : null!,
        "reactive_mechanical" => observed > 0.5
            ? "mechanical backoff pattern after rate limiting"
            : null!,
        _ => $"{dim}: observed={observed:F2} expected≈{expected:F2}"
    };
}
