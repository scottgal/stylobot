using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     GuardAtom (per Taxonomy.md) that runs FIRST (Priority 3, Wave 0, no
///     required signals) and checks raw UA / IP against the pattern reputation
///     cache. Confirmed-good or manually-allowed patterns produce a strong
///     human contribution; confirmed-bad or manually-blocked patterns produce
///     an early-exit-eligible bot contribution. Neutral / suspect patterns are
///     left for the later ranking atom.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>FastPathReputationContributor</c>. Priority 3 -- ahead of the
///         UA sensor, so it reads UA + client IP straight from HttpContext
///         rather than from the sink.
///     </para>
///     <para>
///         State-vs-signal: pattern IDs (UA hash, IP CIDR) are Model-2 hints
///         because downstream verdict composition needs the reputation state /
///         score / support values to justify the fast-path outcome. Same
///         grade of identifier as <c>PrimarySignature</c>, which already lives
///         in the sink. No raw UA or raw IP ever hits the sink from here.
///     </para>
///     <para>
///         Browser-attestation, tool-family, and LAN carve-outs preserve the
///         staging incident guarantees (see CLAUDE.md notes on Sec-Fetch-Site
///         downgrade + IpIsLocal clamp).
///     </para>
/// </remarks>
public sealed class FastPathReputationAtom : DetectorAtomBase
{
    private readonly ILogger<FastPathReputationAtom> _logger;
    private readonly IPatternReputationCache _reputationCache;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public FastPathReputationAtom(
        ILogger<FastPathReputationAtom> logger,
        IPatternReputationCache reputationCache,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "FastPathReputation", category: "FastPathReputation")
    {
        _logger = logger;
        _reputationCache = reputationCache;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 3;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double FastAbortWeight => _configProvider.GetParameter(Name, "fast_abort_weight", 3.0);
    private double BrowserAttestationMaxConfidence
        => _configProvider.GetParameter(Name, "browser_attestation_max_confidence", 0.4);
    private double BrowserAttestationWeight
        => _configProvider.GetParameter(Name, "browser_attestation_weight", 0.8);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        // Ledger: every request enters the fast-path check.

        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var userAgent = context.Request.Headers.UserAgent.ToString();
        var clientIp = context.Connection.RemoteIpAddress?.ToString();

        PatternReputation? matchedPattern = null;
        var matchType = string.Empty;
        var isGoodPattern = false;

        // Raw UA lookup first (most common fast-path hit).
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            var uaPatternId = PatternNormalization.CreateUaPatternId(userAgent);
            var uaReputation = _reputationCache.Get(uaPatternId);

            if (uaReputation?.CanTriggerFastAllow == true)
            {
                matchedPattern = uaReputation;
                matchType = "UserAgent";
                isGoodPattern = true;
            }
            else if (uaReputation?.CanTriggerFastAbort == true)
            {
                matchedPattern = uaReputation;
                matchType = "UserAgent";
                isGoodPattern = false;
            }
        }

        // Skip IP lookup for loopback / RFC1918 -- their accumulated reputation
        // reflects test traffic. Match falls through to neutral.
        var isLocalIp = IsLocalIp(clientIp);

        if (matchedPattern is null && !string.IsNullOrWhiteSpace(clientIp) && !isLocalIp)
        {
            var ipPatternId = PatternNormalization.CreateIpPatternId(clientIp);
            var ipReputation = _reputationCache.Get(ipPatternId);

            if (ipReputation?.CanTriggerFastAllow == true)
            {
                matchedPattern = ipReputation;
                matchType = "IP";
                isGoodPattern = true;
            }
            else if (ipReputation?.CanTriggerFastAbort == true)
            {
                matchedPattern = ipReputation;
                matchType = "IP";
                isGoodPattern = false;
            }
        }

        if (matchedPattern is null)
            return Task.FromResult(Single(
                DetectionContribution.Info(Name, Category, "No known patterns in reputation cache")));

        return Task.FromResult(isGoodPattern
            ? CreateFastAllowContribution(sink, sessionId, matchedPattern, matchType)
            : CreateFastAbortContribution(sink, sessionId, context, matchedPattern, matchType));
    }

    private IReadOnlyList<DetectionContribution> CreateFastAllowContribution(
        SignalSink sink,
        string sessionId,
        PatternReputation matched,
        string matchType)
    {
        _logger.LogInformation(
            "Fast-path reputation allow: {PatternId} ({MatchType}) state={State} score={Score:F2} support={Support:F0}",
            matched.PatternId, matchType, matched.State, matched.BotScore, matched.Support);

        var mt = matchType.ToLowerInvariant();
        sink.Raise(SignalKeys.ReputationFastPathHit, sessionId);
        sink.Raise(SignalKeys.ReputationCanAllow, sessionId);
        sink.Raise($"reputation.fastpath.{mt}.pattern_id:{matched.PatternId}", sessionId);
        sink.Raise($"reputation.fastpath.{mt}.state:{matched.State}", sessionId);
        sink.Raise($"reputation.fastpath.{mt}.score:{matched.BotScore.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"reputation.fastpath.{mt}.support:{matched.Support.ToString("F0", CultureInfo.InvariantCulture)}", sessionId);

        return Single(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = -0.8,
            Weight = 2.5,
            Reason = $"Previously verified as safe ({matchType} seen {matched.Support:F0} times)"
        });
    }

    private IReadOnlyList<DetectionContribution> CreateFastAbortContribution(
        SignalSink sink,
        string sessionId,
        HttpContext context,
        PatternReputation matched,
        string matchType)
    {
        _logger.LogWarning(
            "Fast-path reputation abort: {PatternId} ({MatchType}) state={State} score={Score:F2} support={Support:F0}",
            matched.PatternId, matchType, matched.State, matched.BotScore, matched.Support);

        var mt = matchType.ToLowerInvariant();
        var secFetchSite = context.Request.Headers["Sec-Fetch-Site"].FirstOrDefault();
        var hasBrowserAttestation = !string.IsNullOrEmpty(secFetchSite);

        sink.Raise(SignalKeys.ReputationFastPathHit, sessionId);
        sink.Raise(SignalKeys.ReputationCanAbort, sessionId);
        sink.Raise(SignalKeys.ReputationFastAbortActive, sessionId);
        sink.Raise($"reputation.fastpath.{mt}.pattern_id:{matched.PatternId}", sessionId);
        sink.Raise($"reputation.fastpath.{mt}.state:{matched.State}", sessionId);
        sink.Raise($"reputation.fastpath.{mt}.score:{matched.BotScore.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"reputation.fastpath.{mt}.support:{matched.Support.ToString("F0", CultureInfo.InvariantCulture)}", sessionId);

        var ipIsLocal = sink.ReadBoolHint(SignalKeys.IpIsLocal);
        var isToolFamily = IsToolFamily(sink);

        // IP + no attestation + not tool + not LAN -> VerifiedBot early-exit
        if (matchType == "IP" && !hasBrowserAttestation && !isToolFamily && !ipIsLocal)
        {
            return Single(DetectionContribution.VerifiedBot(
                    Name,
                    $"Previously identified as bot ({matchType} seen {matched.Support:F0} times)")
                with
                {
                    ConfidenceDelta = matched.BotScore,
                    Weight = FastAbortWeight
                });
        }

        // Browser-attestation carve-out -> downgrade to mild bias
        if (hasBrowserAttestation)
        {
            _logger.LogInformation(
                "Fast-path reputation downgraded: {PatternId} has Sec-Fetch-Site: {Site} - using mild bias instead of strong abort",
                matched.PatternId, secFetchSite);

            return Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = Math.Min(matched.BotScore, BrowserAttestationMaxConfidence),
                Weight = BrowserAttestationWeight,
                Reason = $"Reputation bias ({matchType} seen {matched.Support:F0} times as bot, downgraded - browser attestation present)",
                BotType = BotType.Unknown.ToString()
            });
        }

        // UA patterns: strong signal but NO early exit.
        return Single(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = Math.Min(matched.BotScore, 0.6),
            Weight = 1.5,
            Reason = $"Previously identified as bot ({matchType} seen {matched.Support:F0} times)",
            BotType = BotType.Unknown.ToString()
        });
    }

    /// <summary>
    ///     True when the contemporaneous UA classification resolves to
    ///     <see cref="BotType.Tool"/>. Reads the sink hint first; falls back
    ///     to the seed catalog by bot name if the type hint isn't attached
    ///     yet (early-Wave-0 partial-pipeline case).
    /// </summary>
    private static bool IsToolFamily(SignalSink sink)
    {
        var typeHint = sink.ReadHint(SignalKeys.UserAgentBotType);
        if (string.Equals(typeHint, nameof(BotType.Tool), StringComparison.Ordinal))
            return true;

        var nameHint = sink.ReadHint(SignalKeys.UserAgentBotName);
        if (string.IsNullOrEmpty(nameHint)) return false;

        var catalogType = Definitions.BotPatterns.BotPatternLoader.Default.FindBotTypeByName(nameHint);
        return string.Equals(catalogType, nameof(BotType.Tool), StringComparison.Ordinal);
    }

    private static bool IsLocalIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return false;
        if (ip is "::1" or "127.0.0.1" or "localhost") return true;
        if (ip.StartsWith("192.168.", StringComparison.Ordinal)) return true;
        if (ip.StartsWith("10.", StringComparison.Ordinal)) return true;
        if (ip.StartsWith("172.", StringComparison.Ordinal))
        {
            var parts = ip.Split('.');
            return parts.Length >= 2
                   && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var second)
                   && second is >= 16 and <= 31;
        }
        return false;
    }
}
