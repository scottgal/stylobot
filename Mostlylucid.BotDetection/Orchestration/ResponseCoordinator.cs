using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.KeyedSequential;
using Mostlylucid.Ephemeral.Atoms.SlidingCache;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Configuration for the response detection coordinator.
/// </summary>
public sealed class ResponseCoordinatorOptions
{
    /// <summary>
    ///     Maximum number of client IDs to track in response window.
    ///     Default: 5000
    /// </summary>
    public int MaxClientsInWindow { get; set; } = 5000;

    /// <summary>
    ///     Time window for tracking response behavior per client.
    ///     Default: 10 minutes
    /// </summary>
    public TimeSpan ResponseWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Maximum responses to track per client.
    ///     Default: 200
    /// </summary>
    public int MaxResponsesPerClient { get; set; } = 200;

    /// <summary>
    ///     TTL for client response tracking atoms.
    ///     Default: 20 minutes
    /// </summary>
    public TimeSpan ClientTtl { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    ///     Minimum responses before computing scores.
    ///     Default: 3
    /// </summary>
    public int MinResponsesForScoring { get; set; } = 3;

    /// <summary>
    ///     Enable signal emission for observability.
    ///     Default: true
    /// </summary>
    public bool EnableSignals { get; set; } = true;

    /// <summary>
    ///     Trigger configuration for when response analysis activates
    /// </summary>
    public ResponseAnalysisTrigger Trigger { get; set; } = new();

    /// <summary>
    ///     Body pattern matchers (name -> regex pattern)
    /// </summary>
    public Dictionary<string, string> BodyPatterns { get; set; } = new()
    {
        ["stack_trace_marker"] = @"Exception in thread|Stack trace|Traceback \(most recent call last\)",
        ["generic_error_message"] = "An unexpected error occurred",
        ["login_failed_message"] = "Invalid username or password|Login failed|Authentication failed",
        ["rate_limited_message"] = "Too many requests|Rate limit exceeded|Slow down",
        ["ip_blocked_message"] = "Your IP has been blocked|Access denied for this IP|Forbidden"
    };

    /// <summary>
    ///     Honeypot endpoints (paths that should never be accessed)
    /// </summary>
    public List<string> HoneypotPaths { get; set; } = new()
    {
        "/__test-hp",
        "/.git/",
        "/.env",
        "/wp-admin/install.php",
        "/phpmyadmin"
    };

    /// <summary>
    ///     Feature weights for response score computation
    /// </summary>
    public ResponseFeatureWeights FeatureWeights { get; set; } = new();
}

/// <summary>
///     Weights for different response features in scoring
/// </summary>
public sealed class ResponseFeatureWeights
{
    public double FourXxRatio { get; set; } = 0.2;
    public double FourOhFourScan { get; set; } = 0.35;
    public double FiveXxAnomaly { get; set; } = 0.3;
    public double AuthStruggle { get; set; } = 0.2;
    public double HoneypotHit { get; set; } = 0.8;
    public double ErrorTemplate { get; set; } = 0.25;
    public double AbuseFeedback { get; set; } = 0.3;
}

/// <summary>
///     Aggregated response behavior for a single client across multiple responses.
/// </summary>
public sealed record ClientResponseBehavior
{
    public required string ClientId { get; init; }
    public required List<ResponseSignal> Responses { get; init; }
    public required int TotalResponses { get; init; }
    public required int Count2xx { get; init; }
    public required int Count3xx { get; init; }
    public required int Count4xx { get; init; }
    public required int Count5xx { get; init; }
    public required int Count404 { get; init; }
    public required int UniqueNotFoundPaths { get; init; }
    public required int AuthFailures { get; init; }
    public required int HoneypotHits { get; init; }
    public required IReadOnlyDictionary<string, int> PatternCounts { get; init; }
    public required double ResponseScore { get; init; }
    public required DateTime FirstSeen { get; init; }
    public required DateTime LastSeen { get; init; }
}

/// <summary>
///     Signal emitted when response analysis completes for a client.
/// </summary>
public readonly record struct ResponseAnalysisSignal(
    string ClientId,
    double ResponseScore,
    int TotalResponses,
    string Reason,
    DateTime Timestamp);

/// <summary>
///     Out-of-request coordinator for response analysis.
///     Runs AFTER responses are sent (async) or inline for critical paths.
///     Feeds back into heuristic for next request from same client.
/// </summary>
public sealed class ResponseCoordinator : IAsyncDisposable
{
    // Per-client sequential processing
    private readonly KeyedSequentialAtom<ResponseSignal, string> _analysisAtom;

    // TTL-aware cache of client response tracking atoms
    private readonly SlidingCacheAtom<string, ClientResponseTrackingAtom> _clientCache;

    // Internal signal sink owned by this coordinator
    private readonly SignalSink _signals;

    // Feedback callback to heuristic system
    private readonly Action<string, double>? _heuristicFeedback;
    private readonly ILogger<ResponseCoordinator> _logger;
    private readonly ResponseCoordinatorOptions _options;

    public ResponseCoordinator(
        ILogger<ResponseCoordinator> logger,
        IOptions<BotDetectionOptions> options,
        Action<string, double>? heuristicFeedback = null)
    {
        _logger = logger;
        _options = options.Value.ResponseCoordinator ?? new ResponseCoordinatorOptions();
        _heuristicFeedback = heuristicFeedback;

        // Initialize internal signal sink owned by this coordinator
        _signals = new SignalSink(
            _options.MaxClientsInWindow * 10,
            _options.ResponseWindow);

        // Initialize client cache with TTL + LRU
        _clientCache = new SlidingCacheAtom<string, ClientResponseTrackingAtom>(
            async (clientId, ct) =>
            {
                _logger.LogDebug("Creating new ClientResponseTrackingAtom for client: {ClientId}", clientId);
                return await Task.FromResult(
                    new ClientResponseTrackingAtom(clientId, _options, _logger));
            },
            _options.ClientTtl,
            _options.ClientTtl * 2,
            _options.MaxClientsInWindow,
            Environment.ProcessorCount,
            10,
            _signals);

        // Initialize sequential processing atom
        _analysisAtom = new KeyedSequentialAtom<ResponseSignal, string>(
            signal => signal.ClientId,
            async (signal, ct) => await ProcessResponseSignalAsync(signal, ct),
            Environment.ProcessorCount * 2,
            1,
            true,
            _signals);

        _logger.LogInformation(
            "ResponseCoordinator initialized: window={Window}, maxClients={MaxClients}, ttl={Ttl}",
            _options.ResponseWindow,
            _options.MaxClientsInWindow,
            _options.ClientTtl);
    }

    public async ValueTask DisposeAsync()
    {
        await _analysisAtom.DrainAsync();
        await _analysisAtom.DisposeAsync();
        await _clientCache.DisposeAsync();

        _logger.LogInformation("ResponseCoordinator disposed");
    }

    /// <summary>
    ///     Record a response signal for analysis.
    ///     Called by middleware after response is sent (or inline if configured).
    /// </summary>
    public async Task RecordResponseAsync(
        ResponseSignal signal,
        CancellationToken cancellationToken = default)
    {
        // Check if we should analyze this response
        if (!_options.Trigger.ShouldAnalyze(
                signal.ClientId,
                signal.Path,
                signal.StatusCode,
                signal.RequestBotProbability))
        {
            _logger.LogTrace(
                "Skipping response analysis for {ClientId} on {Path} (status={Status}, botProb={BotProb:F2})",
                signal.ClientId, signal.Path, signal.StatusCode, signal.RequestBotProbability);
            return;
        }

        // Enqueue for sequential processing per client
        await _analysisAtom.EnqueueAsync(signal, cancellationToken);
    }

    /// <summary>
    ///     Process a single response signal.
    ///     Runs sequentially per client but parallel across clients.
    /// </summary>
    private async Task ProcessResponseSignalAsync(
        ResponseSignal signal,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get or create tracking atom
            var atom = await _clientCache.GetOrComputeAsync(signal.ClientId, cancellationToken);

            // Record the response
            await atom.RecordResponseAsync(signal, cancellationToken);

            // Get updated behavior
            var behavior = await atom.GetBehaviorAsync(cancellationToken);

            // Emit analysis signal to coordinator-owned sink
            if (_options.EnableSignals)
                _signals.Raise($"response.analysis.{signal.ClientId}", signal.ClientId);

            // Feed back into heuristic if score is significant
            if (behavior.ResponseScore > 0.3 && _heuristicFeedback != null)
            {
                _logger.LogDebug(
                    "Feeding response score back to heuristic: {ClientId} -> {Score:F2}",
                    signal.ClientId,
                    behavior.ResponseScore);

                _heuristicFeedback(signal.ClientId, behavior.ResponseScore);
            }

            // Log significant findings
            if (behavior.ResponseScore > 0.6)
                _logger.LogWarning(
                    "High response score for {ClientId}: {Score:F2} " +
                    "(4xx={FourXx}, 404s={FourOhFour}, 5xx={FiveXx}, honeypot={Honeypot}, patterns={Patterns})",
                    signal.ClientId,
                    behavior.ResponseScore,
                    behavior.Count4xx,
                    behavior.Count404,
                    behavior.Count5xx,
                    behavior.HoneypotHits,
                    string.Join(",", behavior.PatternCounts.Keys));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to process response signal for {ClientId}",
                signal.ClientId);
            throw;
        }
    }

    /// <summary>
    ///     Get response behavior for a client
    /// </summary>
    public async Task<ClientResponseBehavior?> GetClientBehaviorAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        if (!_clientCache.TryGet(clientId, out var atom) || atom == null)
            return null;

        return await atom.GetBehaviorAsync(cancellationToken);
    }

    /// <summary>
    ///     Get recent analysis signals from coordinator-owned sink.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetAnalysisSignals()
    {
        return _signals.Sense(e => e.Signal.StartsWith("response.analysis."));
    }

    /// <summary>
    ///     Build human-readable reason string from behavior
    /// </summary>
    private static string BuildReasonString(ClientResponseBehavior behavior)
    {
        var reasons = new List<string>();

        if (behavior.Count4xx > behavior.TotalResponses * 0.5)
            reasons.Add($"{behavior.Count4xx} 4xx responses");

        if (behavior.Count404 > 10)
            reasons.Add($"{behavior.Count404} 404s across {behavior.UniqueNotFoundPaths} paths");

        if (behavior.Count5xx > 5)
            reasons.Add($"{behavior.Count5xx} 5xx errors");

        if (behavior.HoneypotHits > 0)
            reasons.Add($"{behavior.HoneypotHits} honeypot hits");

        if (behavior.AuthFailures > 5)
            reasons.Add($"{behavior.AuthFailures} auth failures");

        foreach (var (pattern, count) in behavior.PatternCounts.Where(kvp => kvp.Value > 2))
            reasons.Add($"{count}x {pattern}");

        return reasons.Count > 0 ? string.Join("; ", reasons) : "normal behavior";
    }
}

/// <summary>
///     Tracks response behavior for a single client using sliding window.
/// </summary>
internal sealed class ClientResponseTrackingAtom : IDisposable
{
    private readonly string _clientId;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger _logger;
    private readonly ResponseCoordinatorOptions _options;

    private readonly LinkedList<ResponseSignal> _responses;

    private ClientResponseBehavior? _cachedBehavior;

    public ClientResponseTrackingAtom(
        string clientId,
        ResponseCoordinatorOptions options,
        ILogger logger)
    {
        _clientId = clientId;
        _options = options;
        _logger = logger;
        _responses = new LinkedList<ResponseSignal>();
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    public async Task RecordResponseAsync(
        ResponseSignal signal,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Add to window
            _responses.AddLast(signal);

            // Evict old responses
            var cutoff = DateTimeOffset.UtcNow - _options.ResponseWindow;
            while (_responses.Count > 0 && _responses.First!.Value.Timestamp < cutoff) _responses.RemoveFirst();

            // Enforce max responses
            while (_responses.Count > _options.MaxResponsesPerClient) _responses.RemoveFirst();

            // Recompute behavior
            _cachedBehavior = ComputeBehavior();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ClientResponseBehavior> GetBehaviorAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return _cachedBehavior ?? ComputeBehavior();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Compute response behavior metrics and score.
    ///     This is where response-side bot detection happens.
    /// </summary>
    private ClientResponseBehavior ComputeBehavior()
    {
        if (_responses.Count == 0)
            return new ClientResponseBehavior
            {
                ClientId = _clientId,
                Responses = new List<ResponseSignal>(),
                TotalResponses = 0,
                Count2xx = 0,
                Count3xx = 0,
                Count4xx = 0,
                Count5xx = 0,
                Count404 = 0,
                UniqueNotFoundPaths = 0,
                AuthFailures = 0,
                HoneypotHits = 0,
                PatternCounts = new Dictionary<string, int>(),
                ResponseScore = 0,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow
            };

        var responseList = _responses.ToList();
        var firstSeen = responseList.First().Timestamp.UtcDateTime;
        var lastSeen = responseList.Last().Timestamp.UtcDateTime;

        // Count status codes
        var count2xx = responseList.Count(r => r.StatusCode >= 200 && r.StatusCode < 300);
        var count3xx = responseList.Count(r => r.StatusCode >= 300 && r.StatusCode < 400);
        var count4xx = responseList.Count(r => r.StatusCode >= 400 && r.StatusCode < 500);
        var count5xx = responseList.Count(r => r.StatusCode >= 500);
        var count404 = responseList.Count(r => r.StatusCode == 404);

        // Count unique 404 paths
        var unique404Paths = responseList
            .Where(r => r.StatusCode == 404)
            .Select(r => r.Path)
            .Distinct()
            .Count();

        // Count auth failures (401/403)
        var authFailures = responseList.Count(r => r.StatusCode == 401 || r.StatusCode == 403);

        // Count honeypot hits
        var honeypotHits = responseList.Count(r =>
            _options.HoneypotPaths.Any(hp => r.Path.StartsWith(hp, StringComparison.OrdinalIgnoreCase)));

        // Count pattern matches
        var patternCounts = new Dictionary<string, int>();
        foreach (var response in responseList)
        foreach (var pattern in response.BodySummary.MatchedPatterns)
            patternCounts[pattern] = patternCounts.GetValueOrDefault(pattern) + 1;

        // Compute response score
        var responseScore = ComputeResponseScore(
            responseList.Count,
            count2xx,
            count3xx,
            count4xx,
            count5xx,
            count404,
            unique404Paths,
            authFailures,
            honeypotHits,
            patternCounts);

        return new ClientResponseBehavior
        {
            ClientId = _clientId,
            Responses = responseList,
            TotalResponses = responseList.Count,
            Count2xx = count2xx,
            Count3xx = count3xx,
            Count4xx = count4xx,
            Count5xx = count5xx,
            Count404 = count404,
            UniqueNotFoundPaths = unique404Paths,
            AuthFailures = authFailures,
            HoneypotHits = honeypotHits,
            PatternCounts = patternCounts,
            ResponseScore = responseScore,
            FirstSeen = firstSeen,
            LastSeen = lastSeen
        };
    }

    /// <summary>
    ///     Compute bot probability score from response features.
    ///     Returns 0.0 (human-like) to 1.0 (bot-like).
    /// </summary>
    private double ComputeResponseScore(
        int total,
        int count2xx,
        int count3xx,
        int count4xx,
        int count5xx,
        int count404,
        int unique404Paths,
        int authFailures,
        int honeypotHits,
        IReadOnlyDictionary<string, int> patternCounts)
    {
        if (total < _options.MinResponsesForScoring)
            return 0.0;

        var score = 0.0;
        var weights = _options.FeatureWeights;

        // 4xx ratio (excluding normal 404s from old links)
        var fourXxRatio = (double)count4xx / total;
        if (fourXxRatio > 0.7)
            score += weights.FourXxRatio * fourXxRatio;

        // 404 scan pattern (many unique 404 paths)
        if (count404 > 15 && unique404Paths > 10)
        {
            var scanScore = Math.Min(1.0, unique404Paths / 50.0);
            score += weights.FourOhFourScan * scanScore;
        }

        // 5xx anomaly (triggering server errors)
        var fiveXxRatio = (double)count5xx / total;
        if (fiveXxRatio > 0.3)
            score += weights.FiveXxAnomaly * fiveXxRatio;

        // Auth struggle (repeated auth failures)
        if (authFailures > 10)
        {
            var authScore = Math.Min(1.0, authFailures / 30.0);
            score += weights.AuthStruggle * authScore;
        }

        // Honeypot hits (VERY strong signal)
        if (honeypotHits > 0)
            score += weights.HoneypotHit * Math.Min(1.0, honeypotHits / 3.0);

        // Error template patterns
        var errorPatterns = patternCounts.Where(kvp =>
            kvp.Key.Contains("error") || kvp.Key.Contains("stack_trace")).Sum(kvp => kvp.Value);
        if (errorPatterns > 3)
            score += weights.ErrorTemplate * Math.Min(1.0, errorPatterns / 10.0);

        // Abuse feedback patterns (rate limit, IP blocked messages)
        var abusePatterns = patternCounts.Where(kvp =>
            kvp.Key.Contains("rate_limit") || kvp.Key.Contains("blocked")).Sum(kvp => kvp.Value);
        if (abusePatterns > 2)
            score += weights.AbuseFeedback * Math.Min(1.0, abusePatterns / 5.0);

        return Math.Min(score, 1.0);
    }
}