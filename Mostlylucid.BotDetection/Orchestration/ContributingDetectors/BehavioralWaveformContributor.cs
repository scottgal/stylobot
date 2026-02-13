using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Behavioral waveform analysis contributor.
///     Analyzes request patterns over time to detect bot behavior patterns.
///     Best-in-breed approach:
///     - Request timing analysis (too regular = bot, too random = human)
///     - Path traversal patterns (depth-first vs breadth-first)
///     - Request rate analysis (bursts vs steady)
///     - Session behavior tracking (cookie consistency, session lifetime)
///     - Mouse/keyboard interaction patterns (from client-side signals)
///     This runs late and correlates signals across multiple requests from the same signature.
///     Raises signals:
///     - waveform.request_interval_stddev
///     - waveform.request_rate
///     - waveform.path_pattern
///     - waveform.timing_regularity_score
///     - waveform.burst_detection
/// </summary>
public partial class BehavioralWaveformContributor : ContributingDetectorBase
{
    // Cache request history per signature for waveform analysis
    private const string CacheKeyPrefix = "waveform:";
    private const int MaxHistorySize = 100; // Keep last 100 requests per signature
    private static readonly TimeSpan HistoryExpiration = TimeSpan.FromMinutes(30);
    private readonly IMemoryCache _cache;
    private readonly ILogger<BehavioralWaveformContributor> _logger;

    public BehavioralWaveformContributor(
        ILogger<BehavioralWaveformContributor> logger,
        IMemoryCache cache)
    {
        _logger = logger;
        _cache = cache;
    }

    public override string Name => "BehavioralWaveform";
    public override int Priority => 3; // Run late, after individual detectors

    // Requires basic Wave 0 detection to have completed (UA signal is always present after Wave 0)
    public override IReadOnlyList<TriggerCondition> TriggerConditions => new TriggerCondition[]
    {
        new SignalExistsTrigger(SignalKeys.UserAgent)
    };

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var signals = ImmutableDictionary.CreateBuilder<string, object>();

        try
        {
            // Get or create signature for this client
            var signature = GetClientSignature(state);
            signals.Add(SignalKeys.WaveformSignature, signature);

            // Get request history for this signature
            var history = GetOrCreateHistory(signature);

            // Add current request to history
            var currentRequest = new RequestSnapshot
            {
                Timestamp = DateTimeOffset.UtcNow,
                Path = state.HttpContext.Request.Path.ToString(),
                Method = state.HttpContext.Request.Method,
                StatusCode = state.HttpContext.Response.StatusCode,
                UserAgent = state.HttpContext.Request.Headers.UserAgent.ToString(),
                RefererHash = GetRefererHash(state.HttpContext.Request.Headers.Referer.ToString())
            };

            history.Add(currentRequest);

            // Analyze timing patterns
            AnalyzeTimingPatterns(history, contributions, signals);

            // Analyze path traversal patterns
            AnalyzePathPatterns(history, contributions, signals);

            // Analyze request rate and bursts
            AnalyzeRequestRate(history, contributions, signals);

            // Analyze session behavior
            AnalyzeSessionBehavior(state, history, contributions, signals);

            // Analyze mouse/keyboard interaction signals (if available from client-side)
            AnalyzeInteractionPatterns(state, contributions, signals);

            // Update cache with new history
            UpdateHistory(signature, history);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in behavioral waveform analysis");
            signals.Add("waveform.analysis_error", ex.Message);
        }

        // Always add at least one contribution
        if (contributions.Count == 0)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = 0.0,
                Weight = 1.0,
                Reason = "Behavioral waveform analysis complete (insufficient history)",
                Signals = signals.ToImmutable()
            });
        }
        else
        {
            // Ensure last contribution has all signals
            var last = contributions[^1];
            contributions[^1] = last with { Signals = signals.ToImmutable() };
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private void AnalyzeTimingPatterns(List<RequestSnapshot> history, List<DetectionContribution> contributions,
        ImmutableDictionary<string, object>.Builder signals)
    {
        if (history.Count < 3) return; // Need at least 3 requests for timing analysis

        // Calculate intervals between requests
        var intervals = new List<double>();
        for (var i = 1; i < history.Count; i++)
        {
            var interval = (history[i].Timestamp - history[i - 1].Timestamp).TotalSeconds;
            intervals.Add(interval);
        }

        if (intervals.Count == 0) return;

        // Calculate standard deviation of intervals
        var mean = intervals.Average();
        var variance = intervals.Sum(x => Math.Pow(x - mean, 2)) / intervals.Count;
        var stdDev = Math.Sqrt(variance);

        signals.Add("waveform.interval_mean", mean);
        signals.Add("waveform.interval_stddev", stdDev);

        // Coefficient of variation (CV = stddev / mean)
        var cv = mean > 0 ? stdDev / mean : 0;
        signals.Add(SignalKeys.WaveformTimingRegularity, cv);

        // Very low CV = too regular = likely bot
        if (cv < 0.15 && intervals.Count >= 5)
            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", 0.7,
                $"Highly regular timing pattern (CV={cv:F3}) - typical bot behavior",
                weight: 1.6,
                botType: BotType.Scraper.ToString()));
        // Moderate CV = human-like
        else if (cv >= 0.3 && cv <= 2.0)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = -0.15,
                Weight = 1.3,
                Reason = $"Natural timing variation detected (CV={cv:F3})",
                Signals = signals.ToImmutable()
            });

        // Check for burst patterns (many requests in short time)
        var recentRequests = history.Where(r => r.Timestamp > DateTimeOffset.UtcNow.AddSeconds(-10)).Count();
        if (recentRequests >= 10)
        {
            signals.Add(SignalKeys.WaveformBurstDetected, true);
            signals.Add("waveform.burst_size", recentRequests);

            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", 0.65,
                $"Burst pattern detected: {recentRequests} requests in 10 seconds",
                weight: 1.5,
                botType: BotType.Scraper.ToString()));
        }
    }

    private void AnalyzePathPatterns(List<RequestSnapshot> history, List<DetectionContribution> contributions,
        ImmutableDictionary<string, object>.Builder signals)
    {
        if (history.Count < 5) return;

        var recentPaths = history.TakeLast(20).Select(r => r.Path).ToList();

        // Calculate path diversity
        var uniquePaths = recentPaths.Distinct().Count();
        var pathDiversity = (double)uniquePaths / recentPaths.Count;
        signals.Add(SignalKeys.WaveformPathDiversity, pathDiversity);

        // Very low diversity = scanning/crawling same paths
        if (pathDiversity < 0.3 && recentPaths.Count >= 10)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = 0.3,
                Weight = 1.2,
                Reason = $"Low path diversity ({pathDiversity:P0}) - possible automated scanning",
                Signals = signals.ToImmutable()
            });

        // Detect sequential/systematic path traversal (e.g., /page/1, /page/2, /page/3)
        var sequentialPattern = DetectSequentialPattern(recentPaths);
        if (sequentialPattern)
        {
            signals.Add("waveform.sequential_pattern", true);

            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", 0.6,
                "Sequential path traversal detected (systematic crawling pattern)",
                weight: 1.4,
                botType: BotType.Scraper.ToString()));
        }

        // Depth-first vs breadth-first traversal analysis
        var traversalPattern = AnalyzeTraversalPattern(recentPaths);
        signals.Add("waveform.traversal_pattern", traversalPattern);

        if (traversalPattern == "depth-first-strict")
            // Bots often do strict depth-first (go deep, then backtrack)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = 0.25,
                Weight = 1.1,
                Reason = "Strict depth-first traversal (common for crawlers)",
                Signals = signals.ToImmutable()
            });
    }

    private void AnalyzeRequestRate(List<RequestSnapshot> history, List<DetectionContribution> contributions,
        ImmutableDictionary<string, object>.Builder signals)
    {
        if (history.Count < 2) return;

        var timeSpan = (history[^1].Timestamp - history[0].Timestamp).TotalMinutes;
        if (timeSpan <= 0) return;

        var requestRate = history.Count / timeSpan; // requests per minute
        signals.Add("waveform.request_rate", requestRate);

        // Very high rate = likely bot
        if (requestRate > 30) // 30+ requests/minute
            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", 0.75,
                $"High request rate: {requestRate:F1} requests/minute",
                weight: 1.7,
                botType: BotType.Scraper.ToString()));
        else if (requestRate > 10) // 10-30 requests/minute
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = 0.3,
                Weight = 1.3,
                Reason = $"Elevated request rate: {requestRate:F1} requests/minute",
                Signals = signals.ToImmutable()
            });
    }

    private void AnalyzeSessionBehavior(BlackboardState state, List<RequestSnapshot> history,
        List<DetectionContribution> contributions, ImmutableDictionary<string, object>.Builder signals)
    {
        // Check if User-Agent changes across requests (suspicious)
        var userAgents = history.Select(r => r.UserAgent).Distinct().Count();
        signals.Add("waveform.user_agent_changes", userAgents);

        if (userAgents > 1 && history.Count >= 5)
            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", 0.8,
                $"User-Agent changed {userAgents} times in session (IP rotation or spoofing)",
                weight: 1.8,
                botType: BotType.MaliciousBot.ToString()));

        // Session duration analysis
        if (history.Count >= 2)
        {
            var sessionDuration = (history[^1].Timestamp - history[0].Timestamp).TotalMinutes;
            signals.Add("waveform.session_duration_minutes", sessionDuration);

            // Very short session with many requests = bot
            if (sessionDuration < 1 && history.Count >= 10)
                contributions.Add(DetectionContribution.Bot(
                    Name, "Waveform", 0.7,
                    $"High-speed session: {history.Count} requests in {sessionDuration:F1} minutes",
                    weight: 1.6,
                    botType: BotType.Scraper.ToString()));
        }
    }

    private void AnalyzeInteractionPatterns(BlackboardState state, List<DetectionContribution> contributions,
        ImmutableDictionary<string, object>.Builder signals)
    {
        // Check for client-side interaction signals (mouse movement, keyboard events)
        // These would be sent from JavaScript tracking

        if (state.Signals.TryGetValue(SignalKeys.ClientMouseEvents, out var mouseEvents) &&
            mouseEvents is int mouseCount)
        {
            signals.Add("waveform.mouse_events", mouseCount);

            if (mouseCount == 0)
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Waveform",
                    ConfidenceDelta = 0.4,
                    Weight = 1.5,
                    Reason = "No mouse movement detected (headless browser indicator)",
                    Signals = signals.ToImmutable()
                });
        }

        if (state.Signals.TryGetValue(SignalKeys.ClientKeyboardEvents, out var keyboardEvents) &&
            keyboardEvents is int keyCount)
            signals.Add("waveform.keyboard_events", keyCount);
    }

    private string GetClientSignature(BlackboardState state)
    {
        // Create signature from IP + User-Agent hash
        var ip = state.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = state.HttpContext.Request.Headers.UserAgent.ToString();
        return $"{ip}:{GetHash(ua)}";
    }

    private List<RequestSnapshot> GetOrCreateHistory(string signature)
    {
        var cacheKey = CacheKeyPrefix + signature;
        return _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = HistoryExpiration;
            return new List<RequestSnapshot>();
        })!;
    }

    private void UpdateHistory(string signature, List<RequestSnapshot> history)
    {
        // Keep only last N requests
        while (history.Count > MaxHistorySize) history.RemoveAt(0);

        var cacheKey = CacheKeyPrefix + signature;
        _cache.Set(cacheKey, history, HistoryExpiration);
    }

    private bool DetectSequentialPattern(List<string> paths)
    {
        if (paths.Count < 3) return false;

        var numbers = paths.Select(p => NumberPattern().Match(p))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Value))
            .ToList();

        if (numbers.Count < 3) return false;

        // Check if numbers are sequential (difference of 1 or -1)
        for (var i = 1; i < numbers.Count; i++)
        {
            var diff = Math.Abs(numbers[i] - numbers[i - 1]);
            if (diff != 1) return false;
        }

        return true;
    }

    private string AnalyzeTraversalPattern(List<string> paths)
    {
        if (paths.Count < 5) return "insufficient-data";

        // Depth-first: paths get progressively deeper, then jump back
        // Example: /a, /a/b, /a/b/c, /x, /x/y
        var depths = paths.Select(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries).Length).ToList();

        var increasingRuns = 0;
        var strictDepthFirst = true;

        for (var i = 1; i < depths.Count; i++)
            if (depths[i] > depths[i - 1])
                increasingRuns++;
            else if (depths[i] < depths[i - 1] - 1) strictDepthFirst = false;

        if (increasingRuns > paths.Count * 0.7) return strictDepthFirst ? "depth-first-strict" : "depth-first-loose";

        return "mixed";
    }

    private static string GetHash(string input)
    {
        return input.Length > 0 ? input.GetHashCode().ToString("X8") : "empty";
    }

    private static string GetRefererHash(string referer)
    {
        return string.IsNullOrEmpty(referer) ? "none" : GetHash(referer);
    }

    private class RequestSnapshot
    {
        public DateTimeOffset Timestamp { get; init; }
        public string Path { get; init; } = string.Empty;
        public string Method { get; init; } = string.Empty;
        public int StatusCode { get; init; }
        public string UserAgent { get; init; } = string.Empty;
        public string RefererHash { get; init; } = string.Empty;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();
}