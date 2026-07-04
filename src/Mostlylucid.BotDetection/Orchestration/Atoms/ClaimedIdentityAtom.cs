using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Lifecycle;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that tests whether observed request
///     behaviour is consistent with the UA family claimed in the User-Agent
///     header.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>ClaimedIdentityContributor</c>. A UA string is a testable
///         claim: Chrome should send Sec-Fetch headers, Accept-Language, and
///         request from a residential IP. A bot claiming to be Chrome will
///         score low against Chrome's behavioural centroid.
///     </para>
///     <para>
///         Centroids are seeded from <c>ua_profiles.yaml</c> and auto-adapt
///         via EWM (alpha=0.99). Restarting resets to seeds by design -- the
///         YAML values are "likely human" starting shapes, not commandments.
///     </para>
///     <para>
///         Signals emitted (Model 2 hints -- family/tier are public labels,
///         score is a number, no PII in any value):
///         <c>claimed_identity.ran</c>,
///         <see cref="SignalKeys.ClaimedIdentityHasProfile"/>,
///         <see cref="SignalKeys.ClaimedIdentityFamily"/>,
///         <see cref="SignalKeys.ClaimedIdentityTier"/>,
///         <see cref="SignalKeys.ClaimedIdentityConsistencyScore"/>.
///         Observed dimension dictionary stays internal to the atom -- it is
///         not persistable state.
///     </para>
///     <para>
///         Closed-loop feedback gates: refuse to learn from requests where
///         stylobot's own enforcement shaped the response, upstream is
///         unhealthy, gateway is still in cold-start warmup, or the request
///         was load-shed. Preserves the pre-fix behaviour when the optional
///         gates aren't wired.
///     </para>
/// </remarks>
public sealed class ClaimedIdentityAtom : DetectorAtomBase
{
    private readonly ILogger<ClaimedIdentityAtom> _logger;
    private readonly UaProfileStore _profileStore;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly UpstreamHealthGate? _upstreamHealth;
    private readonly GatewayWarmupGate? _gatewayWarmup;

    public ClaimedIdentityAtom(
        ILogger<ClaimedIdentityAtom> logger,
        IDetectorConfigProvider configProvider,
        UaProfileStore profileStore,
        IHttpContextAccessor httpContextAccessor,
        UpstreamHealthGate? upstreamHealth = null,
        GatewayWarmupGate? gatewayWarmup = null)
        : base(name: "ClaimedIdentity", category: "ClaimedIdentity")
    {
        _logger = logger;
        _configProvider = configProvider;
        _profileStore = profileStore;
        _httpContextAccessor = httpContextAccessor;
        _upstreamHealth = upstreamHealth;
        _gatewayWarmup = gatewayWarmup;
    }

    public override int Priority => 35;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.UserAgentFamily };

    private double ConsistencyThreshold => _configProvider.GetParameter(Name, "consistency_threshold", 0.55);
    private double BotSignalConfidence => _configProvider.GetParameter(Name, "bot_signal_confidence", 0.35);
    private double UpdateAlpha => _configProvider.GetParameter(Name, "update_alpha", 0.99);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        // Always ledger the run so downstream can distinguish "not checked"
        // from "checked, no value" (rule: ran-vs-value).

        var rawFamily = sink.ReadHint(SignalKeys.UserAgentFamily);
        if (string.IsNullOrEmpty(rawFamily))
            return Task.FromResult(None());

        // Skip known bots -- UserAgent atoms already handle them. Emit a
        // presence signal for the has-profile=false path so the composer
        // sees a definite negative rather than absence.
        if (sink.ReadBoolHint(SignalKeys.UserAgentIsBot))
        {
            sink.Raise($"{SignalKeys.ClaimedIdentityHasProfile}:false", sessionId);
            return Task.FromResult(Single(
                DetectionContribution.Info(Name, Category, "UA already identified as known bot")));
        }

        var centroid = _profileStore.GetCentroid(rawFamily);
        if (centroid == null)
        {
            sink.Raise($"{SignalKeys.ClaimedIdentityHasProfile}:false", sessionId);
            return Task.FromResult(Single(
                DetectionContribution.Info(Name, Category, $"No profile for UA family '{rawFamily}'")));
        }

        sink.Raise($"{SignalKeys.ClaimedIdentityHasProfile}:true", sessionId);
        sink.Raise($"{SignalKeys.ClaimedIdentityFamily}:{centroid.Family}", sessionId);
        sink.Raise($"{SignalKeys.ClaimedIdentityTier}:{centroid.Tier}", sessionId);

        // Observed dimensions dict stays INTERNAL to this atom -- per the
        // state-vs-signal rule, dicts of numeric arms with dim names are
        // "rich state" not signals. Downstream atoms that want the shape
        // query UaProfileStore, not the sink.
        var observed = ObserveDimensions(sink);
        var consistencyScore = ComputeConsistency(observed, centroid);

        sink.Raise($"{SignalKeys.ClaimedIdentityConsistencyScore}:{consistencyScore.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

        var signature = sink.ReadHint(SignalKeys.PrimarySignature) ?? "";
        _profileStore.RecordSignature(signature, centroid.Family, centroid.Tier, consistencyScore);

        // Auto-update centroid iff request looks genuine AND passes every
        // closed-loop feedback gate. Rule: transitions become learning, not
        // repeats -- prevents bots from poisoning the centroid.
        if (ShouldUpdateCentroid(centroid, consistencyScore, sink))
            _profileStore.UpdateCentroid(rawFamily, observed);

        if (consistencyScore < ConsistencyThreshold)
        {
            var reason = DescribeInconsistency(observed, centroid, consistencyScore);
            _logger.LogDebug(
                "ClaimedIdentity: {Family} consistency={Score:F2} < threshold={Threshold:F2}: {Reason}",
                centroid.Family, consistencyScore, ConsistencyThreshold, reason);
            return Task.FromResult(Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = BotSignalConfidence,
                Weight = 1.0,
                Reason = $"{centroid.Family} behavioral profile mismatch (consistency={consistencyScore:F2}): {reason}",
                BotType = BotType.Unknown.ToString()
            }));
        }

        return Task.FromResult(Single(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = -0.15,
            Weight = 1.0,
            Reason = $"{centroid.Family} behavioral profile consistent (score={consistencyScore:F2})"
        }));
    }

    private Dictionary<string, float> ObserveDimensions(SignalSink sink)
    {
        var request = _httpContextAccessor.HttpContext?.Request;
        var dims = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        // sec_fetch_present: W3C Sec-Fetch-Mode or Sec-Fetch-Site header present
        var secFetchMode = sink.ReadHint(SignalKeys.HeaderSecFetchMode);
        dims["sec_fetch_present"] = string.IsNullOrEmpty(secFetchMode) ? 0f : 1f;

        // accept_html: Accept header includes text/html
        var accept = request?.Headers["Accept"].ToString() ?? "";
        dims["accept_html"] = accept.Contains("text/html", StringComparison.OrdinalIgnoreCase) ? 1f : 0f;

        // accept_language / accept_encoding: header presence
        dims["accept_language"] = request?.Headers.ContainsKey("Accept-Language") == true ? 1f : 0f;
        dims["accept_encoding"] = request?.Headers.ContainsKey("Accept-Encoding") == true ? 1f : 0f;

        // datacenter_ip: IP is a datacenter/VPN address
        dims["datacenter_ip"] = sink.ReadBoolHint(SignalKeys.IpIsDatacenter) ? 1f : 0f;

        // Reactive dimensions: only include if enough error events
        var errorCount = sink.ReadIntHint(SignalKeys.ReactiveErrorEventCount);
        if (errorCount >= 2)
        {
            var compliance = (float)sink.ReadDoubleHint(SignalKeys.ReactiveRetryAfterCompliance, -1);
            if (compliance > 0)
            {
                // Near 1.0 = mechanical precision (bot-like). Distance from 1.0 = human-like.
                var precision = (float)Math.Max(0.0, 1.0 - Math.Abs(compliance - 1.0));
                dims["reactive_precision"] = precision;
            }

            var backoffPattern = sink.ReadHint(SignalKeys.ReactiveBackoffPattern) ?? "none";
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
            if (!observed.TryGetValue(dim, out var obs)) continue;
            var similarity = 1.0 - Math.Abs(obs - liveDim.Mean);
            totalWeight += liveDim.Weight;
            totalScore += liveDim.Weight * similarity;
        }

        return totalWeight > 0 ? totalScore / totalWeight : 0.5;
    }

    /// <summary>
    ///     Four-axis closed-loop refuse rule for centroid updates:
    ///     response.from_upstream, UpstreamHealthGate, GatewayWarmupGate,
    ///     BotDetectionShedKey. UA centroids are slow-decaying (EWM alpha=0.99,
    ///     ~100 samples to drift 50%) so a single hour of shed/throttle/block
    ///     traffic biases the prior for days.
    /// </summary>
    internal bool ShouldUpdateCentroid(LiveCentroid centroid, double consistencyScore, SignalSink sink)
    {
        if (consistencyScore < 0.65) return false;

        // Browser-tier profiles: DC IPs are suspicious -- don't update with them
        if (centroid.Tier == "browser" && sink.ReadBoolHint(SignalKeys.IpIsDatacenter))
            return false;

        // response.from_upstream absent OR "false" -> stylobot synthesised the
        // response (load-shed 503, policy block 403, throttle 429, honeypot
        // 404, API-key reject). Not a fair sample.
        //
        // Prior contributor used state.GetSignal<bool?>(...) ?? true -- absent
        // meant "assume true". Preserve semantics: absence => healthy, presence
        // must be literal true. Model-2 hint parse: readBool defaults false;
        // treat absent as true to keep parity with the contributor.
        var fromUpstreamHint = sink.ReadHint(SignalKeys.ResponseFromUpstream);
        if (fromUpstreamHint is not null && !fromUpstreamHint.Equals("true", StringComparison.OrdinalIgnoreCase))
            return false;

        if (_upstreamHealth is not null && !_upstreamHealth.IsUpstreamHealthy())
            return false;

        if (_gatewayWarmup is not null && !_gatewayWarmup.IsWarmedUp())
            return false;

        // Belt-and-braces: if the load-shed marker is on HttpContext.Items, the
        // orchestrator normally short-circuits before we run, but a future
        // codepath might not. Fail closed.
        if (_httpContextAccessor.HttpContext?.Items.ContainsKey(BotDetectionMiddleware.BotDetectionShedKey) == true)
            return false;

        return true;
    }

    private static string DescribeInconsistency(Dictionary<string, float> observed, LiveCentroid centroid, double score)
    {
        var violations = new List<string>(4);
        foreach (var (dim, liveDim) in centroid.Dimensions.OrderByDescending(d => d.Value.Weight))
        {
            if (!observed.TryGetValue(dim, out var obs)) continue;
            var deviation = Math.Abs(obs - liveDim.Mean);
            if (deviation > 0.4)
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
