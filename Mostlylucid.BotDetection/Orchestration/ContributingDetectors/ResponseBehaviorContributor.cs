using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Response behavior analysis contributor.
///     Integrates response-side bot detection from ResponseCoordinator into the detection pipeline.
///     This contributor feeds BACK into the request detection pipeline by analyzing patterns
///     from previous responses for the same client (IP+UA signature).
///     Best-in-breed approach:
///     - Honeypot path detection (accessing paths that should never be hit)
///     - 404 scanning patterns (probing for vulnerabilities)
///     - Error template harvesting (triggering stack traces/debug info)
///     - Auth brute-forcing detection (repeated 401s)
///     - Rate limit violations (429 responses)
///     - Response time anomalies (too fast = cached/automated)
///     This runs early (wave 0) to provide feedback for current request based on past behavior.
///     Configuration loaded from: responsebehavior.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:ResponseBehaviorContributor:*
/// </summary>
public class ResponseBehaviorContributor : ConfiguredContributorBase
{
    private readonly ResponseCoordinator? _coordinator;
    private readonly ILogger<ResponseBehaviorContributor> _logger;

    public ResponseBehaviorContributor(
        ILogger<ResponseBehaviorContributor> logger,
        IDetectorConfigProvider configProvider,
        ResponseCoordinator? coordinator = null)
        : base(configProvider)
    {
        _logger = logger;
        _coordinator = coordinator;
    }

    public override string Name => "ResponseBehavior";
    public override int Priority => Manifest?.Priority ?? 12;

    // No triggers - runs in first wave
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven thresholds — no magic numbers
    private int ScanHeavyCount404 => GetParam("scan_heavy_count_404", 8);
    private int ScanHeavyUniquePaths => GetParam("scan_heavy_unique_paths", 5);
    private int ScanModerateCount404 => GetParam("scan_moderate_count_404", 4);
    private int ScanModerateUniquePaths => GetParam("scan_moderate_unique_paths", 3);
    private int ScanLightUniquePaths => GetParam("scan_light_unique_paths", 2);
    private double ScanHeavyWeight => GetParam("scan_heavy_weight", 2.0);
    private double ScanModerateConfidence => GetParam("scan_moderate_confidence", 0.4);
    private double ScanModerateWeight => GetParam("scan_moderate_weight", 1.5);
    private double ScanLightConfidence => GetParam("scan_light_confidence", 0.15);
    private double ScanLightWeight => GetParam("scan_light_weight", 1.2);
    private int AuthSevereThreshold => GetParam("auth_severe_threshold", 20);
    private int AuthModerateThreshold => GetParam("auth_moderate_threshold", 10);
    private int AuthMildThreshold => GetParam("auth_mild_threshold", 5);
    private int ErrorHarvestingHighThreshold => GetParam("error_harvesting_high_threshold", 10);
    private int ErrorHarvestingModerateThreshold => GetParam("error_harvesting_moderate_threshold", 5);
    private int RateLimitHighThreshold => GetParam("rate_limit_high_threshold", 5);
    private int RateLimitModerateThreshold => GetParam("rate_limit_moderate_threshold", 2);
    private double HighResponseScoreThreshold => GetParam("high_response_score_threshold", 0.8);
    private double MediumResponseScoreThreshold => GetParam("medium_response_score_threshold", 0.6);
    private double LowResponseScoreThreshold => GetParam("low_response_score_threshold", 0.4);
    private double CleanHistoryThreshold => GetParam("clean_history_threshold", 0.2);
    private int CleanHistoryMinResponses => GetParam("clean_history_min_responses", 5);

    // Honeypot confidence/weight
    private double HoneypotConfidence => GetParam("honeypot_confidence", 0.9);
    private double HoneypotWeight => GetParam("honeypot_weight", 2.0);

    // Auth brute-force confidence/weight per tier
    private double AuthSevereConfidence => GetParam("auth_severe_confidence", 0.85);
    private double AuthSevereWeight => GetParam("auth_severe_weight", 1.9);
    private double AuthModerateConfidence => GetParam("auth_moderate_confidence", 0.5);
    private double AuthModerateWeight => GetParam("auth_moderate_weight", 1.5);
    private double AuthMildConfidence => GetParam("auth_mild_confidence", 0.2);
    private double AuthMildWeight => GetParam("auth_mild_weight", 1.2);

    // Error harvesting confidence/weight
    private double ErrorHarvestingHighConfidence => GetParam("error_harvesting_high_confidence", 0.7);
    private double ErrorHarvestingHighWeight => GetParam("error_harvesting_high_weight", 1.6);
    private double ErrorHarvestingModerateConfidence => GetParam("error_harvesting_moderate_confidence", 0.3);
    private double ErrorHarvestingModerateWeight => GetParam("error_harvesting_moderate_weight", 1.3);

    // Rate limit confidence/weight
    private double RateLimitHighConfidence => GetParam("rate_limit_high_confidence", 0.75);
    private double RateLimitHighWeight => GetParam("rate_limit_high_weight", 1.7);
    private double RateLimitModerateConfidence => GetParam("rate_limit_moderate_confidence", 0.4);
    private double RateLimitModerateWeight => GetParam("rate_limit_moderate_weight", 1.4);

    // Overall score confidence/weight
    private double HighScoreConfidence => GetParam("high_score_confidence", 0.85);
    private double HighScoreWeight => GetParam("high_score_weight", 1.8);
    private double MediumScoreConfidence => GetParam("medium_score_confidence", 0.5);
    private double MediumScoreWeight => GetParam("medium_score_weight", 1.5);
    private double LowScoreConfidence => GetParam("low_score_confidence", 0.2);
    private double LowScoreWeight => GetParam("low_score_weight", 1.2);
    private double CleanHistoryConfidence => GetParam("clean_history_confidence", -0.15);
    private double CleanHistoryWeight => GetParam("clean_history_weight", 1.3);

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            // Check if ResponseCoordinator is available
            if (_coordinator == null)
            {
                state.WriteSignal(SignalKeys.ResponseCoordinatorAvailable, false);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Response",
                    ConfidenceDelta = 0.0,
                    Weight = 0.0,
                    Reason = "Response coordinator not configured"
                });
                return contributions;
            }

            state.WriteSignal(SignalKeys.ResponseCoordinatorAvailable, true);

            // Get client signature (IP + UA hash)
            var clientSignature = GetClientSignature(state.HttpContext);
            state.WriteSignal(SignalKeys.ResponseClientSignature, clientSignature);

            // Fetch historical response behavior
            var behavior = await _coordinator.GetClientBehaviorAsync(clientSignature, cancellationToken);

            if (behavior == null || behavior.TotalResponses == 0)
            {
                state.WriteSignals([
                    new(SignalKeys.ResponseHasHistory, false),
                    new(SignalKeys.ResponseTotalResponses, 0)
                ]);

                // No history - neutral contribution
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Response",
                    ConfidenceDelta = 0.0,
                    Weight = 1.0,
                    Reason = "No response history for client"
                });
                return contributions;
            }

            state.WriteSignals([
                new(SignalKeys.ResponseHasHistory, true),
                new(SignalKeys.ResponseTotalResponses, behavior.TotalResponses),
                new(SignalKeys.ResponseHistoricalScore, behavior.ResponseScore)
            ]);

            // Check for programmatic request attestation — if present, downweight
            // response history signals since auth failures and rate limits may have
            // been caused by bot detection itself (feedback loop), not the server.
            var isProgrammatic = state.Signals.TryGetValue(SignalKeys.ProgrammaticRequest, out var progVal)
                                 && progVal is true;

            // Analyze historical behavior patterns
            // Honeypot hits are ALWAYS significant — no attestation can excuse accessing /.env
            AnalyzeHoneypotHits(state, behavior, contributions);
            AnalyzeScanPatterns(state, behavior, contributions);

            // Auth failures and rate limits are downweighted for programmatic requests
            // because they may be caused by bot detection blocking (feedback loop)
            if (!isProgrammatic)
            {
                AnalyzeAuthStruggle(state, behavior, contributions);
                AnalyzeRateLimitViolations(state, behavior, contributions);
            }
            else
            {
                // Still write signals for visibility, but don't contribute to score
                state.WriteSignal(SignalKeys.ResponseAuthFailures, behavior.AuthFailures);
                var rateLimitCount = behavior.PatternCounts
                    .Where(kvp => kvp.Key.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) ||
                                  kvp.Key.Contains("blocked", StringComparison.OrdinalIgnoreCase))
                    .Sum(kvp => kvp.Value);
                state.WriteSignal(SignalKeys.ResponseRateLimitViolations, rateLimitCount);

                if (behavior.AuthFailures > 0 || rateLimitCount > 0)
                {
                    _logger.LogDebug(
                        "Skipping auth/rate-limit scoring for programmatic request (auth={Auth}, rateLimit={RateLimit})",
                        behavior.AuthFailures, rateLimitCount);
                    contributions.Add(DetectionContribution.Info(
                        Name, "Response",
                        $"Auth/rate-limit history skipped — programmatic request attestation present (auth={behavior.AuthFailures}, rateLimit={rateLimitCount})"));
                }
            }

            AnalyzeErrorHarvesting(state, behavior, contributions);
            AnalyzeOverallResponseScore(state, behavior, contributions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing response behavior");
            state.WriteSignal("response.analysis_error", ex.Message); // Not in SignalKeys — transient error info only
        }

        // Always add at least one contribution
        if (contributions.Count == 0)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Response",
                ConfidenceDelta = 0.0,
                Weight = 1.0,
                Reason = "Response behavior analysis complete (no significant patterns)"
            });
        }

        return contributions;
    }

    /// <summary>
    ///     Analyze honeypot path hits - accessing paths that should never be accessed
    /// </summary>
    private void AnalyzeHoneypotHits(
        BlackboardState state,
        ClientResponseBehavior behavior,
        List<DetectionContribution> contributions)
    {
        if (behavior.HoneypotHits == 0)
            return;

        state.WriteSignal(SignalKeys.ResponseHoneypotHits, behavior.HoneypotHits);

        // ANY honeypot hit is a very strong bot signal
        contributions.Add(DetectionContribution.Bot(
            Name, "Response", HoneypotConfidence,
            $"Client accessed {behavior.HoneypotHits} honeypot path(s) in previous requests",
            weight: HoneypotWeight,
            botType: BotType.Scraper.ToString()));
    }

    /// <summary>
    ///     Analyze 404 scanning patterns - systematic probing for vulnerabilities
    /// </summary>
    private void AnalyzeScanPatterns(
        BlackboardState state,
        ClientResponseBehavior behavior,
        List<DetectionContribution> contributions)
    {
        state.WriteSignals([
            new(SignalKeys.ResponseCount404, behavior.Count404),
            new(SignalKeys.ResponseUnique404Paths, behavior.UniqueNotFoundPaths)
        ]);

        // Real humans almost never hit multiple unique 404 paths.
        // A single 404 from a stale bookmark is normal; 3+ unique 404 paths is scanning.

        // HEAVY: Systematic vulnerability scanning — many unique 404 paths
        if (behavior.Count404 > ScanHeavyCount404 && behavior.UniqueNotFoundPaths > ScanHeavyUniquePaths)
        {
            state.WriteSignal(SignalKeys.ResponseScanPatternDetected, true);

            var scanIntensity = Math.Min(1.0, behavior.UniqueNotFoundPaths / 20.0);
            var confidence = 0.6 + scanIntensity * 0.3; // 0.6 to 0.9

            contributions.Add(DetectionContribution.Bot(
                Name, "Response", confidence,
                $"Systematic scanning detected: {behavior.Count404} 404s across {behavior.UniqueNotFoundPaths} unique paths",
                weight: ScanHeavyWeight,
                botType: BotType.Scraper.ToString()));
        }
        // MODERATE: Probable scanning — several unique 404 paths
        else if (behavior.Count404 >= ScanModerateCount404 && behavior.UniqueNotFoundPaths >= ScanModerateUniquePaths)
        {
            state.WriteSignal(SignalKeys.ResponseScanPatternDetected, true);

            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Response",
                ConfidenceDelta = ScanModerateConfidence,
                Weight = ScanModerateWeight,
                Reason = $"Probable scanning: {behavior.Count404} page-not-found errors across {behavior.UniqueNotFoundPaths} different URLs"
            });
        }
        // LIGHT: Early signal — a couple of 404s on unique paths (not stale bookmarks)
        else if (behavior.UniqueNotFoundPaths >= ScanLightUniquePaths)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Response",
                ConfidenceDelta = ScanLightConfidence,
                Weight = ScanLightWeight,
                Reason = $"Multiple 404s on distinct paths: {behavior.Count404} errors on {behavior.UniqueNotFoundPaths} unique URLs"
            });
        }
    }

    /// <summary>
    ///     Analyze authentication failures - credential brute-forcing
    /// </summary>
    private void AnalyzeAuthStruggle(
        BlackboardState state,
        ClientResponseBehavior behavior,
        List<DetectionContribution> contributions)
    {
        state.WriteSignal(SignalKeys.ResponseAuthFailures, behavior.AuthFailures);

        if (behavior.AuthFailures > AuthSevereThreshold)
        {
            state.WriteSignal(SignalKeys.ResponseAuthStruggle, "severe");

            contributions.Add(DetectionContribution.Bot(
                Name, "Response", AuthSevereConfidence,
                $"Severe login brute-forcing: {behavior.AuthFailures} failed login attempts",
                weight: AuthSevereWeight,
                botType: BotType.MaliciousBot.ToString()));
        }
        else if (behavior.AuthFailures > AuthModerateThreshold)
        {
            state.WriteSignal(SignalKeys.ResponseAuthStruggle, "moderate");

            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Response",
                ConfidenceDelta = AuthModerateConfidence,
                Weight = AuthModerateWeight,
                Reason = $"Repeated login failures: {behavior.AuthFailures} failed attempts"
            });
        }
        else if (behavior.AuthFailures > AuthMildThreshold)
        {
            state.WriteSignal(SignalKeys.ResponseAuthStruggle, "mild");

            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Response",
                ConfidenceDelta = AuthMildConfidence,
                Weight = AuthMildWeight,
                Reason = $"Some login failures: {behavior.AuthFailures} failed attempts"
            });
        }
    }

    /// <summary>
    ///     Analyze error template harvesting - triggering stack traces to gather info
    /// </summary>
    private void AnalyzeErrorHarvesting(
        BlackboardState state,
        ClientResponseBehavior behavior,
        List<DetectionContribution> contributions)
    {
        // Look for error patterns in response bodies
        var errorPatternCount = behavior.PatternCounts
            .Where(kvp => kvp.Key.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                          kvp.Key.Contains("stack_trace", StringComparison.OrdinalIgnoreCase))
            .Sum(kvp => kvp.Value);

        state.WriteSignal(SignalKeys.ResponseErrorPatternCount, errorPatternCount);

        if (errorPatternCount > ErrorHarvestingHighThreshold)
        {
            state.WriteSignal(SignalKeys.ResponseErrorHarvesting, true);

            contributions.Add(DetectionContribution.Bot(
                Name, "Response", ErrorHarvestingHighConfidence,
                $"Error harvesting detected: {errorPatternCount} error/stack trace patterns",
                weight: ErrorHarvestingHighWeight,
                botType: BotType.Scraper.ToString()));
        }
        else if (errorPatternCount > ErrorHarvestingModerateThreshold)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Response",
                ConfidenceDelta = ErrorHarvestingModerateConfidence,
                Weight = ErrorHarvestingModerateWeight,
                Reason = $"Triggering errors: {errorPatternCount} error patterns"
            });
        }
    }

    /// <summary>
    ///     Analyze rate limit violations
    /// </summary>
    private void AnalyzeRateLimitViolations(
        BlackboardState state,
        ClientResponseBehavior behavior,
        List<DetectionContribution> contributions)
    {
        var rateLimitCount = behavior.PatternCounts
            .Where(kvp => kvp.Key.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) ||
                          kvp.Key.Contains("blocked", StringComparison.OrdinalIgnoreCase))
            .Sum(kvp => kvp.Value);

        state.WriteSignal(SignalKeys.ResponseRateLimitViolations, rateLimitCount);

        if (rateLimitCount > RateLimitHighThreshold)
            contributions.Add(DetectionContribution.Bot(
                Name, "Response", RateLimitHighConfidence,
                $"Multiple rate limit violations: {rateLimitCount} occurrences",
                weight: RateLimitHighWeight,
                botType: BotType.Scraper.ToString()));
        else if (rateLimitCount > RateLimitModerateThreshold)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Response",
                ConfidenceDelta = RateLimitModerateConfidence,
                Weight = RateLimitModerateWeight,
                Reason = $"Exceeded request speed limits {rateLimitCount} times"
            });
    }

    /// <summary>
    ///     Analyze overall response score from coordinator
    /// </summary>
    private void AnalyzeOverallResponseScore(
        BlackboardState state,
        ClientResponseBehavior behavior,
        List<DetectionContribution> contributions)
    {
        // The ResponseCoordinator already computed a comprehensive score
        if (behavior.ResponseScore > HighResponseScoreThreshold)
            contributions.Add(DetectionContribution.Bot(
                Name, "Response", HighScoreConfidence,
                $"Very high response score: {behavior.ResponseScore:F2}",
                weight: HighScoreWeight,
                botType: BotType.MaliciousBot.ToString()));
        else if (behavior.ResponseScore > MediumResponseScoreThreshold)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Response",
                ConfidenceDelta = MediumScoreConfidence,
                Weight = MediumScoreWeight,
                Reason = "Response patterns strongly suggest automated access"
            });
        else if (behavior.ResponseScore > LowResponseScoreThreshold)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Response",
                ConfidenceDelta = LowScoreConfidence,
                Weight = LowScoreWeight,
                Reason = "Response patterns show some signs of automated access"
            });
        // Low score = likely human
        else if (behavior.ResponseScore < CleanHistoryThreshold && behavior.TotalResponses >= CleanHistoryMinResponses)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Response",
                ConfidenceDelta = CleanHistoryConfidence,
                Weight = CleanHistoryWeight,
                Reason =
                    $"Clean response history: {behavior.TotalResponses} responses, score {behavior.ResponseScore:F2}"
            });
    }

    private string GetClientSignature(HttpContext context)
    {
        // Use resolved IP from middleware (handles X-Forwarded-For behind proxies)
        var ip = "unknown";
        if (context.Items.TryGetValue(Middleware.BotDetectionMiddleware.AggregatedEvidenceKey, out var evObj)
            && evObj is AggregatedEvidence evidence
            && evidence.Signals.TryGetValue(SignalKeys.ClientIp, out var ipObj))
        {
            ip = ipObj?.ToString() ?? ip;
        }
        else
        {
            ip = context.Connection.RemoteIpAddress?.ToString() ?? ip;
        }

        var ua = context.Request.Headers.UserAgent.ToString();
        return $"{ip}:{GetHash(ua)}";
    }

    private static string GetHash(string input)
    {
        if (input.Length == 0) return "empty";
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        return System.IO.Hashing.XxHash32.HashToUInt32(bytes).ToString("X8");
    }
}