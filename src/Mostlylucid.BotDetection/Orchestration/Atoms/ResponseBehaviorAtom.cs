using System.IO.Hashing;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that folds historical response
///     behaviour from <see cref="ResponseCoordinator"/> back into the
///     detection pipeline. Priority 12 -- after transport classification
///     so programmatic / API traffic can be identified and downweighted
///     before response-side history is scored.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>ResponseBehaviorContributor</c>. Preserves:
///         <list type="bullet">
///             <item>Honeypot hit detection (always significant, no
///                   attestation carve-out).</item>
///             <item>Fail2ban-style escalating action-policy overrides
///                   for persistent 404 abuse.</item>
///             <item>Exclusive-404 exclusive pattern (all responses 404 =
///                   scanner) suppressed under upstream outage.</item>
///             <item>Heavy / moderate / light 404 scan tiers.</item>
///             <item>Auth-brute-force severity ladder suppressed for
///                   programmatic traffic (feedback loop guard).</item>
///             <item>Error-harvesting + rate-limit tiers.</item>
///             <item>Overall ResponseScore mapping with clean-history
///                   guard (4xx ratio &lt; 50%).</item>
///         </list>
///     </para>
///     <para>
///         The two upstream-health / response-from-upstream gates that
///         guard the 404-derived scan tiers are preserved verbatim -- the
///         atom must not feed stylobot's own enforcement responses back
///         as bot evidence.
///     </para>
/// </remarks>
public sealed class ResponseBehaviorAtom : DetectorAtomBase
{
    private readonly ResponseCoordinator? _coordinator;
    private readonly ILogger<ResponseBehaviorAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ResponseBehaviorAtom(
        ILogger<ResponseBehaviorAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        ResponseCoordinator? coordinator = null)
        : base(name: "ResponseBehavior", category: "Response")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _coordinator = coordinator;
    }

    public override int Priority => 12;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.TransportProtocol };

    private int ScanHeavyCount404 => _configProvider.GetParameter(Name, "scan_heavy_count_404", 8);
    private int ScanHeavyUniquePaths => _configProvider.GetParameter(Name, "scan_heavy_unique_paths", 5);
    private int ScanModerateCount404 => _configProvider.GetParameter(Name, "scan_moderate_count_404", 4);
    private int ScanModerateUniquePaths => _configProvider.GetParameter(Name, "scan_moderate_unique_paths", 3);
    private int ScanLightUniquePaths => _configProvider.GetParameter(Name, "scan_light_unique_paths", 2);
    private double ScanHeavyWeight => _configProvider.GetParameter(Name, "scan_heavy_weight", 2.0);
    private double ScanModerateConfidence => _configProvider.GetParameter(Name, "scan_moderate_confidence", 0.4);
    private double ScanModerateWeight => _configProvider.GetParameter(Name, "scan_moderate_weight", 1.5);
    private double ScanLightConfidence => _configProvider.GetParameter(Name, "scan_light_confidence", 0.15);
    private double ScanLightWeight => _configProvider.GetParameter(Name, "scan_light_weight", 1.2);
    private int AuthSevereThreshold => _configProvider.GetParameter(Name, "auth_severe_threshold", 20);
    private int AuthModerateThreshold => _configProvider.GetParameter(Name, "auth_moderate_threshold", 10);
    private int AuthMildThreshold => _configProvider.GetParameter(Name, "auth_mild_threshold", 5);
    private int ErrorHarvestingHighThreshold => _configProvider.GetParameter(Name, "error_harvesting_high_threshold", 10);
    private int ErrorHarvestingModerateThreshold => _configProvider.GetParameter(Name, "error_harvesting_moderate_threshold", 5);
    private int RateLimitHighThreshold => _configProvider.GetParameter(Name, "rate_limit_high_threshold", 5);
    private int RateLimitModerateThreshold => _configProvider.GetParameter(Name, "rate_limit_moderate_threshold", 2);
    private double HighResponseScoreThreshold => _configProvider.GetParameter(Name, "high_response_score_threshold", 0.8);
    private double MediumResponseScoreThreshold => _configProvider.GetParameter(Name, "medium_response_score_threshold", 0.6);
    private double LowResponseScoreThreshold => _configProvider.GetParameter(Name, "low_response_score_threshold", 0.4);
    private double CleanHistoryThreshold => _configProvider.GetParameter(Name, "clean_history_threshold", 0.2);
    private int CleanHistoryMinResponses => _configProvider.GetParameter(Name, "clean_history_min_responses", 5);
    private double HoneypotConfidence => _configProvider.GetParameter(Name, "honeypot_confidence", 0.9);
    private double HoneypotWeight => _configProvider.GetParameter(Name, "honeypot_weight", 2.0);
    private double AuthSevereConfidence => _configProvider.GetParameter(Name, "auth_severe_confidence", 0.85);
    private double AuthSevereWeight => _configProvider.GetParameter(Name, "auth_severe_weight", 1.9);
    private double AuthModerateConfidence => _configProvider.GetParameter(Name, "auth_moderate_confidence", 0.5);
    private double AuthModerateWeight => _configProvider.GetParameter(Name, "auth_moderate_weight", 1.5);
    private double AuthMildConfidence => _configProvider.GetParameter(Name, "auth_mild_confidence", 0.2);
    private double AuthMildWeight => _configProvider.GetParameter(Name, "auth_mild_weight", 1.2);
    private double ErrorHarvestingHighConfidence => _configProvider.GetParameter(Name, "error_harvesting_high_confidence", 0.7);
    private double ErrorHarvestingHighWeight => _configProvider.GetParameter(Name, "error_harvesting_high_weight", 1.6);
    private double ErrorHarvestingModerateConfidence => _configProvider.GetParameter(Name, "error_harvesting_moderate_confidence", 0.3);
    private double ErrorHarvestingModerateWeight => _configProvider.GetParameter(Name, "error_harvesting_moderate_weight", 1.3);
    private double RateLimitHighConfidence => _configProvider.GetParameter(Name, "rate_limit_high_confidence", 0.75);
    private double RateLimitHighWeight => _configProvider.GetParameter(Name, "rate_limit_high_weight", 1.7);
    private double RateLimitModerateConfidence => _configProvider.GetParameter(Name, "rate_limit_moderate_confidence", 0.4);
    private double RateLimitModerateWeight => _configProvider.GetParameter(Name, "rate_limit_moderate_weight", 1.4);
    private double HighScoreConfidence => _configProvider.GetParameter(Name, "high_score_confidence", 0.85);
    private double HighScoreWeight => _configProvider.GetParameter(Name, "high_score_weight", 1.8);
    private double MediumScoreConfidence => _configProvider.GetParameter(Name, "medium_score_confidence", 0.5);
    private double MediumScoreWeight => _configProvider.GetParameter(Name, "medium_score_weight", 1.5);
    private double LowScoreConfidence => _configProvider.GetParameter(Name, "low_score_confidence", 0.2);
    private double LowScoreWeight => _configProvider.GetParameter(Name, "low_score_weight", 1.2);
    private double CleanHistoryConfidence => _configProvider.GetParameter(Name, "clean_history_confidence", -0.15);
    private double CleanHistoryWeight => _configProvider.GetParameter(Name, "clean_history_weight", 1.3);
    private int Exclusive404MinCount => _configProvider.GetParameter(Name, "exclusive_404_min_count", 3);
    private double Exclusive404Ratio => _configProvider.GetParameter(Name, "exclusive_404_ratio", 0.8);
    private double Exclusive404Confidence => _configProvider.GetParameter(Name, "exclusive_404_confidence", 0.75);
    private double Exclusive404Weight => _configProvider.GetParameter(Name, "exclusive_404_weight", 2.0);
    private bool Fail2BanEnabled => _configProvider.GetParameter(Name, "fail2ban_enabled", true);
    private int Fail2BanThrottleThreshold => _configProvider.GetParameter(Name, "fail2ban_throttle_threshold", 5);
    private int Fail2BanBlockThreshold => _configProvider.GetParameter(Name, "fail2ban_block_threshold", 15);
    private int Fail2BanHardBlockThreshold => _configProvider.GetParameter(Name, "fail2ban_hard_block_threshold", 50);
    private string Fail2BanThrottlePolicy => _configProvider.GetParameter(Name, "fail2ban_throttle_policy", "throttle-stealth");
    private string Fail2BanBlockPolicy => _configProvider.GetParameter(Name, "fail2ban_block_policy", "block");
    private string Fail2BanHardBlockPolicy => _configProvider.GetParameter(Name, "fail2ban_hard_block_policy", "block-hard");

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        sink.Raise("response.behavior.ran", sessionId);

        var contributions = new List<DetectionContribution>();

        try
        {
            if (_coordinator is null)
            {
                sink.Raise($"{SignalKeys.ResponseCoordinatorAvailable}:false", sessionId);
                return Single(DetectionContribution.Info(Name, Category, "Response coordinator not configured"));
            }

            sink.Raise($"{SignalKeys.ResponseCoordinatorAvailable}:true", sessionId);

            var clientSignature = GetClientSignature(context, sink);
            sink.Raise($"{SignalKeys.ResponseClientSignature}:{clientSignature}", sessionId);

            var behavior = await _coordinator.GetClientBehaviorAsync(clientSignature, ct).ConfigureAwait(false);

            if (behavior is null || behavior.TotalResponses == 0)
            {
                sink.Raise($"{SignalKeys.ResponseHasHistory}:false", sessionId);
                sink.Raise($"{SignalKeys.ResponseTotalResponses}:0", sessionId);
                return Single(DetectionContribution.Info(Name, Category, "No response history for client"));
            }

            sink.Raise($"{SignalKeys.ResponseHasHistory}:true", sessionId);
            sink.Raise($"{SignalKeys.ResponseTotalResponses}:{behavior.TotalResponses}", sessionId);
            sink.Raise($"{SignalKeys.ResponseHistoricalScore}:{behavior.ResponseScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}", sessionId);

            var isProgrammatic = sink.ReadBoolHint(SignalKeys.ProgrammaticRequest);
            if (!isProgrammatic)
            {
                var protocolClass = sink.ReadHint(SignalKeys.TransportProtocolClass);
                isProgrammatic = protocolClass is "api" or "grpc" or "signalr";
            }

            AnalyzeHoneypotHits(sink, sessionId, behavior, contributions);
            AnalyzeScanPatterns(sink, sessionId, behavior, contributions);

            if (!isProgrammatic)
            {
                AnalyzeAuthStruggle(sink, sessionId, behavior, contributions);
                AnalyzeRateLimitViolations(sink, sessionId, behavior, contributions);
            }
            else
            {
                sink.Raise($"{SignalKeys.ResponseAuthFailures}:{behavior.AuthFailures}", sessionId);
                var rateLimitCount = behavior.PatternCounts
                    .Where(kvp => kvp.Key.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
                                  || kvp.Key.Contains("blocked", StringComparison.OrdinalIgnoreCase))
                    .Sum(kvp => kvp.Value);
                sink.Raise($"{SignalKeys.ResponseRateLimitViolations}:{rateLimitCount}", sessionId);

                if (behavior.AuthFailures > 0 || rateLimitCount > 0)
                {
                    _logger.LogDebug(
                        "Skipping auth/rate-limit scoring for programmatic request (auth={Auth}, rateLimit={RateLimit})",
                        behavior.AuthFailures, rateLimitCount);
                    contributions.Add(DetectionContribution.Info(Name, Category,
                        $"Auth/rate-limit history skipped - programmatic request attestation present (auth={behavior.AuthFailures}, rateLimit={rateLimitCount})"));
                }
            }

            AnalyzeErrorHarvesting(sink, sessionId, behavior, contributions);
            AnalyzeOverallResponseScore(behavior, contributions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing response behavior");
        }

        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, Category, "Response behavior analysis complete (no significant patterns)"));

        return contributions;
    }

    private void AnalyzeHoneypotHits(SignalSink sink, string sessionId, ClientResponseBehavior behavior, List<DetectionContribution> contributions)
    {
        if (behavior.HoneypotHits == 0) return;
        sink.Raise($"{SignalKeys.ResponseHoneypotHits}:{behavior.HoneypotHits}", sessionId);
        contributions.Add(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = HoneypotConfidence,
            Weight = HoneypotWeight,
            Reason = $"Client accessed {behavior.HoneypotHits} honeypot path(s) in previous requests",
            BotType = BotType.Scraper.ToString()
        });
    }

    private void AnalyzeScanPatterns(SignalSink sink, string sessionId, ClientResponseBehavior behavior, List<DetectionContribution> contributions)
    {
        sink.Raise($"{SignalKeys.ResponseCount404}:{behavior.Count404}", sessionId);
        sink.Raise($"{SignalKeys.ResponseUnique404Paths}:{behavior.UniqueNotFoundPaths}", sessionId);

        var upstreamHealthy = sink.ReadBoolHint(SignalKeys.UpstreamHealthy, fallback: true);
        var fromUpstream = sink.ReadBoolHint(SignalKeys.ResponseFromUpstream, fallback: true);
        var statusArmsActive = upstreamHealthy && fromUpstream;

        if (Fail2BanEnabled && statusArmsActive && behavior.UniqueNotFoundPaths >= 2)
        {
            string? policy = null;
            string reason = string.Empty;
            if (behavior.Count404 >= Fail2BanHardBlockThreshold)
            {
                policy = Fail2BanHardBlockPolicy;
                reason = $"Persistent scanning: {behavior.Count404} 404s across {behavior.UniqueNotFoundPaths} paths - hard block";
            }
            else if (behavior.Count404 >= Fail2BanBlockThreshold)
            {
                policy = Fail2BanBlockPolicy;
                reason = $"Active scanning: {behavior.Count404} 404s across {behavior.UniqueNotFoundPaths} paths - block";
            }
            else if (behavior.Count404 >= Fail2BanThrottleThreshold)
            {
                policy = Fail2BanThrottlePolicy;
                reason = $"Repeat 404s: {behavior.Count404} 404s across {behavior.UniqueNotFoundPaths} paths - throttle";
            }

            if (policy is not null)
            {
                sink.Raise($"{SignalKeys.ActionPolicyTrigger}:{policy}", sessionId);
                sink.Raise($"{SignalKeys.ActionPolicyTriggerReason}:{reason}", sessionId);
                sink.Raise($"{SignalKeys.ActionPolicyEscalationCount}:{behavior.Count404}", sessionId);
                _logger.LogInformation("Fail2ban escalation: {Count}x 404 → {Policy}", behavior.Count404, policy);
            }
        }

        var fourOhFourRatio = behavior.TotalResponses > 0
            ? (double)behavior.Count404 / behavior.TotalResponses : 0;
        if (statusArmsActive && behavior.Count404 >= Exclusive404MinCount && fourOhFourRatio >= Exclusive404Ratio)
        {
            sink.Raise($"{SignalKeys.ResponseScanPatternDetected}:true", sessionId);
            sink.Raise($"{SignalKeys.ResponseExclusive404}:true", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = Exclusive404Confidence,
                Weight = Exclusive404Weight,
                Reason = $"Exclusive 404 pattern: {behavior.Count404}/{behavior.TotalResponses} responses are 404 ({fourOhFourRatio:P0})",
                BotType = BotType.Scraper.ToString()
            });
            return;
        }

        if (!statusArmsActive) return;

        if (behavior.Count404 > ScanHeavyCount404 && behavior.UniqueNotFoundPaths > ScanHeavyUniquePaths)
        {
            sink.Raise($"{SignalKeys.ResponseScanPatternDetected}:true", sessionId);
            var scanIntensity = Math.Min(1.0, behavior.UniqueNotFoundPaths / 20.0);
            var confidence = 0.6 + scanIntensity * 0.3;
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = confidence,
                Weight = ScanHeavyWeight,
                Reason = $"Systematic scanning detected: {behavior.Count404} 404s across {behavior.UniqueNotFoundPaths} unique paths",
                BotType = BotType.Scraper.ToString()
            });
        }
        else if (behavior.Count404 >= ScanModerateCount404 && behavior.UniqueNotFoundPaths >= ScanModerateUniquePaths)
        {
            sink.Raise($"{SignalKeys.ResponseScanPatternDetected}:true", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = ScanModerateConfidence,
                Weight = ScanModerateWeight,
                Reason = $"Probable scanning: {behavior.Count404} page-not-found errors across {behavior.UniqueNotFoundPaths} different URLs",
                BotType = BotType.Unknown.ToString()
            });
        }
        else if (behavior.UniqueNotFoundPaths >= ScanLightUniquePaths)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = ScanLightConfidence,
                Weight = ScanLightWeight,
                Reason = $"Multiple 404s on distinct paths: {behavior.Count404} errors on {behavior.UniqueNotFoundPaths} unique URLs",
                BotType = BotType.Unknown.ToString()
            });
        }
    }

    private void AnalyzeAuthStruggle(SignalSink sink, string sessionId, ClientResponseBehavior behavior, List<DetectionContribution> contributions)
    {
        sink.Raise($"{SignalKeys.ResponseAuthFailures}:{behavior.AuthFailures}", sessionId);
        var upstreamHealthy = sink.ReadBoolHint(SignalKeys.UpstreamHealthy, fallback: true);
        var fromUpstream = sink.ReadBoolHint(SignalKeys.ResponseFromUpstream, fallback: true);
        if (!upstreamHealthy || !fromUpstream) return;

        if (behavior.AuthFailures > AuthSevereThreshold)
        {
            sink.Raise($"{SignalKeys.ResponseAuthStruggle}:severe", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = AuthSevereConfidence,
                Weight = AuthSevereWeight,
                Reason = $"Severe login brute-forcing: {behavior.AuthFailures} failed login attempts",
                BotType = BotType.MaliciousBot.ToString()
            });
        }
        else if (behavior.AuthFailures > AuthModerateThreshold)
        {
            sink.Raise($"{SignalKeys.ResponseAuthStruggle}:moderate", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = AuthModerateConfidence,
                Weight = AuthModerateWeight,
                Reason = $"Repeated login failures: {behavior.AuthFailures} failed attempts",
                BotType = BotType.Unknown.ToString()
            });
        }
        else if (behavior.AuthFailures > AuthMildThreshold)
        {
            sink.Raise($"{SignalKeys.ResponseAuthStruggle}:mild", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = AuthMildConfidence,
                Weight = AuthMildWeight,
                Reason = $"Some login failures: {behavior.AuthFailures} failed attempts",
                BotType = BotType.Unknown.ToString()
            });
        }
    }

    private void AnalyzeErrorHarvesting(SignalSink sink, string sessionId, ClientResponseBehavior behavior, List<DetectionContribution> contributions)
    {
        var errorPatternCount = behavior.PatternCounts
            .Where(kvp => kvp.Key.Contains("error", StringComparison.OrdinalIgnoreCase)
                          || kvp.Key.Contains("stack_trace", StringComparison.OrdinalIgnoreCase))
            .Sum(kvp => kvp.Value);
        sink.Raise($"{SignalKeys.ResponseErrorPatternCount}:{errorPatternCount}", sessionId);

        if (errorPatternCount > ErrorHarvestingHighThreshold)
        {
            sink.Raise($"{SignalKeys.ResponseErrorHarvesting}:true", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = ErrorHarvestingHighConfidence,
                Weight = ErrorHarvestingHighWeight,
                Reason = $"Error harvesting detected: {errorPatternCount} error/stack trace patterns",
                BotType = BotType.Scraper.ToString()
            });
        }
        else if (errorPatternCount > ErrorHarvestingModerateThreshold)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = ErrorHarvestingModerateConfidence,
                Weight = ErrorHarvestingModerateWeight,
                Reason = $"Triggering errors: {errorPatternCount} error patterns",
                BotType = BotType.Unknown.ToString()
            });
        }
    }

    private void AnalyzeRateLimitViolations(SignalSink sink, string sessionId, ClientResponseBehavior behavior, List<DetectionContribution> contributions)
    {
        var rateLimitCount = behavior.PatternCounts
            .Where(kvp => kvp.Key.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
                          || kvp.Key.Contains("blocked", StringComparison.OrdinalIgnoreCase))
            .Sum(kvp => kvp.Value);
        sink.Raise($"{SignalKeys.ResponseRateLimitViolations}:{rateLimitCount}", sessionId);

        if (rateLimitCount > RateLimitHighThreshold)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = RateLimitHighConfidence,
                Weight = RateLimitHighWeight,
                Reason = $"Multiple rate limit violations: {rateLimitCount} occurrences",
                BotType = BotType.Scraper.ToString()
            });
        }
        else if (rateLimitCount > RateLimitModerateThreshold)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = RateLimitModerateConfidence,
                Weight = RateLimitModerateWeight,
                Reason = $"Exceeded request speed limits {rateLimitCount} times",
                BotType = BotType.Unknown.ToString()
            });
        }
    }

    private void AnalyzeOverallResponseScore(ClientResponseBehavior behavior, List<DetectionContribution> contributions)
    {
        if (behavior.ResponseScore > HighResponseScoreThreshold)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = HighScoreConfidence,
                Weight = HighScoreWeight,
                Reason = $"Very high response score: {behavior.ResponseScore:F2}",
                BotType = BotType.MaliciousBot.ToString()
            });
        }
        else if (behavior.ResponseScore > MediumResponseScoreThreshold)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = MediumScoreConfidence,
                Weight = MediumScoreWeight,
                Reason = "Response patterns strongly suggest automated access",
                BotType = BotType.Unknown.ToString()
            });
        }
        else if (behavior.ResponseScore > LowResponseScoreThreshold)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = LowScoreConfidence,
                Weight = LowScoreWeight,
                Reason = "Response patterns show some signs of automated access",
                BotType = BotType.Unknown.ToString()
            });
        }
        else if (behavior.ResponseScore < CleanHistoryThreshold
                 && behavior.TotalResponses >= CleanHistoryMinResponses
                 && behavior.Count4xx < behavior.TotalResponses * 0.5)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = CleanHistoryConfidence,
                Weight = CleanHistoryWeight,
                Reason = $"Clean response history: {behavior.TotalResponses} responses, score {behavior.ResponseScore:F2}"
            });
        }
    }

    private static string GetClientSignature(HttpContext context, SignalSink sink)
    {
        // Prefer the resolved IP from the sink (IpAtom's canonical ClientIp hint).
        // Falls back to the raw connection address only when the sink hint is absent
        // (e.g. run outside the pack pipeline).
        var ip = sink.ReadHint(SignalKeys.ClientIp)
                 ?? context.Connection.RemoteIpAddress?.ToString()
                 ?? "unknown";
        var ua = context.Request.Headers.UserAgent.ToString();
        return $"{ip}:{GetHash(ua)}";
    }

    private static string GetHash(string input)
    {
        if (input.Length == 0) return "empty";
        var bytes = Encoding.UTF8.GetBytes(input);
        return XxHash32.HashToUInt32(bytes).ToString("X8");
    }
}
