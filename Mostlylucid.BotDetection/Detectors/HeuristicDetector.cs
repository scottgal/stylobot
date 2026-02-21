using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Detectors;

/// <summary>
///     Heuristic-based bot detection using dynamically learned weights.
///     Weights are loaded from the WeightStore and updated based on detection feedback.
///     This detector runs fast inference using a simple linear model (logistic regression).
/// </summary>
/// <remarks>
///     <para>
///         The heuristic model uses a <b>dynamic feature dictionary</b> from
///         <see cref="HeuristicFeatureExtractor" />. Features are key-value pairs where:
///         <list type="bullet">
///             <item>Keys are dynamic feature names (e.g., "det:user_agent_detector", "cat:suspicious")</item>
///             <item>Values are feature activations (typically 0.0 to 1.0)</item>
///         </list>
///     </para>
///     <para>
///         The model is a logistic regression classifier:
///         <code>
///         score = bias + Σ(feature[name] * weight[name])
///         probability = sigmoid(score) = 1 / (1 + e^(-score))
///         </code>
///     </para>
///     <para>
///         Weights are:
///         <list type="bullet">
///             <item>Initialized with sensible defaults for known patterns</item>
///             <item>Loaded from <see cref="IWeightStore" /> on startup if available</item>
///             <item>Updated via learning events when detection feedback is received</item>
///             <item>Persisted to survive application restarts</item>
///             <item>New features automatically get default weights and learn over time</item>
///         </list>
///     </para>
///     <para>
///         <b>Two modes of operation:</b>
///         <list type="number">
///             <item>
///                 <b>Early mode:</b> Before other detectors run, uses only basic request
///                 metadata for quick preliminary classification.
///             </item>
///             <item>
///                 <b>Full mode:</b> After <see cref="AggregatedEvidence" /> is available,
///                 uses all features including detector results for final classification.
///             </item>
///         </list>
///     </para>
/// </remarks>
public class HeuristicDetector : IDetector, IDisposable
{
    private const string BiasSignature = "bias";
    private const float DefaultBias = 0.1f;
    private const float DefaultNewFeatureWeight = 0.1f;

    /// <summary>
    ///     Default weights for known feature patterns.
    ///     New features not in this dictionary get <see cref="DefaultNewFeatureWeight" />.
    /// </summary>
    private static readonly Dictionary<string, float> DefaultWeights = new(StringComparer.OrdinalIgnoreCase)
    {
        // Request basics - human-like patterns
        ["req:ua_length"] = -0.3f,
        ["req:header_count"] = -0.3f,
        ["req:cookie_count"] = -0.5f,
        ["req:is_https"] = -0.2f,

        // Header presence - human-like patterns
        ["hdr:accept-language"] = -0.6f,
        ["hdr:accept"] = -0.2f,
        ["hdr:referer"] = -0.4f,
        ["hdr:origin"] = -0.2f,
        ["hdr:x-requested-with"] = -0.3f,
        ["hdr:connection-close"] = 0.3f,

        // Sec-Fetch-* browser attestation — strong human signals (W3C Fetch Metadata)
        // Browsers set these automatically; bots can't easily forge them while maintaining
        // consistency across all detection layers (TLS, TCP, H2 fingerprints).
        ["hdr:sec-fetch-site"] = -0.3f, // Any Sec-Fetch-Site header = browser attestation present
        ["hdr:sec-fetch-mode"] = -0.2f, // Any Sec-Fetch-Mode header
        ["hdr:sec_fetch_same_origin"] = -0.6f, // same-origin fetch = strong human signal

        // Missing header penalties - absence of expected headers is a bot signal
        ["hdr:missing_accept_language"] = 0.4f,
        ["hdr:missing_referer"] = 0.2f,

        // UA patterns - bot indicators
        ["ua:contains_bot"] = 0.9f,
        ["ua:contains_spider"] = 0.8f,
        ["ua:contains_crawler"] = 0.8f,
        ["ua:contains_scraper"] = 0.8f,
        ["ua:headless"] = 0.8f,
        ["ua:phantomjs"] = 0.9f,
        ["ua:selenium"] = 0.7f,
        ["ua:curl"] = 0.6f,
        ["ua:wget"] = 0.6f,
        ["ua:python"] = 0.5f,
        ["ua:scrapy"] = 0.8f,
        ["ua:requests"] = 0.5f,
        ["ua:httpx"] = 0.5f,
        ["ua:aiohttp"] = 0.5f,

        // Browser indicators - human-like patterns
        ["ua:chrome"] = -0.2f,
        ["ua:firefox"] = -0.2f,
        ["ua:safari"] = -0.2f,
        ["ua:edge"] = -0.2f,

        // Empty/missing User-Agent — no real browser omits this
        ["ua:empty"] = 0.7f,

        // Very short User-Agent (< 15 chars) is suspicious
        ["ua:very_short"] = 0.4f,

        // Composite signals - suspicious combinations
        ["combo:browser_no_accept_lang"] = 0.6f, // Browser UA without Accept-Language = spoofed
        ["combo:browser_no_cookies"] = 0.3f, // Browser UA with no cookies

        // HTTP method
        ["req:method_head"] = 0.3f, // HEAD is commonly used by scanners

        // Path probing patterns
        ["path:env_file"] = 0.6f, // .env file scanning
        ["path:dotfile"] = 0.5f, // Hidden/config file access
        ["path:wordpress_probe"] = 0.5f, // WordPress scanning
        ["path:vcs_probe"] = 0.6f, // .git/.svn probing
        ["path:config_probe"] = 0.5f, // Config/admin path probing

        // Accept patterns
        ["accept:wildcard"] = 0.4f,
        ["accept:html"] = -0.3f,
        ["accept:json"] = 0.0f,

        // Client-side fingerprint - STRONG human indicator when present
        ["fp:received"] = -0.7f, // Fingerprint received = strong human signal
        ["fp:legitimate"] = -0.8f, // Legitimate fingerprint = very strong human signal
        ["fp:integrity"] = -0.5f, // Integrity score weight (multiplied by actual score)
        ["fp:suspicious"] = 0.6f, // Suspicious fingerprint (headless, automation)
        ["fp:missing"] = 0.15f, // No fingerprint - slightly suspicious but not conclusive

        // Response behavior signals — historical response patterns
        ["sig:response_honeypot_hits"] = 0.9f, // Honeypot hit = very strong bot signal
        ["sig:response_scan_pattern_detected"] = 0.8f, // Systematic 404 scanning
        ["sig:response_auth_struggle"] = 0.6f, // Auth brute-forcing
        ["sig:response_error_harvesting"] = 0.7f, // Error template harvesting
        ["sig:response_rate_limit_violations"] = 0.7f, // Rate limit violations
        ["sig:response_historical_score"] = 0.5f, // Aggregate response score
        ["sig:response_has_history"] = -0.1f, // Having history is slightly human-like
        ["sig:response_count_404"] = 0.3f, // Some 404s (may be normal)
        ["sig:response_unique_404_paths"] = 0.4f, // Many unique 404 paths = probing

        // Stats - aggregate signals
        ["stat:detector_max"] = 0.6f,
        ["stat:detector_avg"] = 0.3f,
        ["stat:detector_flagged"] = 0.4f,
        ["stat:category_max"] = 0.5f,
        ["stat:category_avg"] = 0.3f,

        // Results - final scores from pipeline
        ["result:bot_probability"] = 1.0f,
        ["result:confidence"] = 0.8f,
        ["result:risk_band"] = 0.6f
    };

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ILogger<HeuristicDetector> _logger;
    private readonly BotDetectionMetrics? _metrics;
    private readonly BotDetectionOptions _options;
    private readonly IWeightStore? _weightStore;
    private float _bias;
    private bool _disposed;
    private bool _initialized;

    // Learned weights (loaded from store, merged with defaults)
    private Dictionary<string, float> _weights = new(StringComparer.OrdinalIgnoreCase);

    public HeuristicDetector(
        ILogger<HeuristicDetector> logger,
        IOptions<BotDetectionOptions> options,
        IWeightStore? weightStore = null,
        BotDetectionMetrics? metrics = null)
    {
        _logger = logger;
        _options = options.Value;
        _weightStore = weightStore;
        _metrics = metrics;
    }

    // Use the canonical signature type from SignatureTypes
    private static string WeightSignatureType => SignatureTypes.HeuristicFeature;

    public string Name => "Heuristic Detector";

    /// <summary>Stage 3: AI/ML - can use all prior signals for learning</summary>
    public DetectorStage Stage => DetectorStage.Intelligence;

    public async Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new DetectorResult();

        // Skip if heuristic detection is disabled
        if (!_options.AiDetection.Heuristic.Enabled)
        {
            stopwatch.Stop();
            return result;
        }

        try
        {
            // Initialize weights on first use
            await EnsureInitializedAsync(cancellationToken);

            // Check if AggregatedEvidence is available (full mode)
            // This happens when other detectors have already run
            var evidence = context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj)
                ? evidenceObj as AggregatedEvidence
                : null;

            Dictionary<string, float> features;
            string mode;

            if (evidence != null && evidence.Contributions.Count > 0)
            {
                // Full mode: Use all features from HeuristicFeatureExtractor
                features = HeuristicFeatureExtractor.ExtractFeatures(context, evidence);
                mode = "full";
            }
            else
            {
                // Early mode: Use only basic request metadata
                features = HeuristicFeatureExtractor.ExtractBasicFeatures(context);
                mode = "early";
            }

            // Run heuristic inference
            var (isBot, probability) = RunInference(features);

            if (isBot)
            {
                result.Confidence = (probability - 0.5) * 2; // Scale 0.5-1.0 to 0-1
                result.BotType = InferBotType(features, evidence);
                result.Reasons.Add(new DetectionReason
                {
                    Category = "Heuristic",
                    Detail = $"Heuristic model ({mode}): {probability:P0} bot likelihood ({features.Count} features)",
                    ConfidenceImpact = result.Confidence
                });
            }
            else
            {
                // Human-like - return negative impact (helps with demo visibility)
                var humanProbability = 1.0 - probability;
                result.Confidence = (0.5 - probability) * 2; // Scale 0-0.5 to 0-1 (inverted)
                result.Reasons.Add(new DetectionReason
                {
                    Category = "Heuristic",
                    Detail =
                        $"Heuristic model ({mode}): {humanProbability:P0} human likelihood ({features.Count} features)",
                    ConfidenceImpact = -result.Confidence // Negative = human indicator
                });
            }

            _logger.LogDebug(
                "Heuristic detection ({Mode}) used {FeatureCount} features, probability={Probability:F3}",
                mode, features.Count, probability);

            stopwatch.Stop();
            _metrics?.RecordDetection(result.Confidence, result.Confidence > _options.BotThreshold, stopwatch.Elapsed,
                Name);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics?.RecordError(Name, ex.GetType().Name);
            _logger.LogWarning(ex, "Heuristic detection failed, continuing without it");
        }

        return result;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            // Start with default weights
            _weights = new Dictionary<string, float>(DefaultWeights, StringComparer.OrdinalIgnoreCase);
            _bias = DefaultBias;

            // Try to load learned weights from store
            if (_weightStore != null && _options.AiDetection.Heuristic.LoadLearnedWeights)
                await LoadWeightsFromStoreAsync(cancellationToken);

            _initialized = true;
            _logger.LogInformation(
                "Heuristic detector initialized with {WeightCount} weights ({Source})",
                _weights.Count, _weightStore != null ? "learned + defaults" : "defaults only");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task LoadWeightsFromStoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Load bias
            var biasWeight = await _weightStore!.GetWeightAsync(WeightSignatureType, BiasSignature, cancellationToken);
            if (Math.Abs(biasWeight) > 0.001) _bias = (float)biasWeight;

            // Load all feature weights for this type
            var allStoredWeights = await _weightStore.GetAllWeightsAsync(WeightSignatureType, cancellationToken);

            var loadedCount = 0;
            foreach (var learnedWeight in allStoredWeights)
                if (learnedWeight.Signature != BiasSignature && Math.Abs(learnedWeight.Weight) > 0.001)
                {
                    _weights[learnedWeight.Signature] = (float)learnedWeight.Weight;
                    loadedCount++;
                }

            if (loadedCount > 0)
                _logger.LogDebug(
                    "Loaded {Count} learned weights from store (bias={Bias:F3})",
                    loadedCount, _bias);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load weights from store, using defaults");
        }
    }

    /// <summary>
    ///     Runs the heuristic linear model inference with dynamic features.
    /// </summary>
    private (bool IsBot, double Probability) RunInference(Dictionary<string, float> features)
    {
        // Calculate weighted sum: score = bias + Σ(feature[name] * weight[name])
        var score = _bias;

        foreach (var (featureName, featureValue) in features)
        {
            // Get weight for this feature (use default if not found)
            var weight = _weights.TryGetValue(featureName, out var w) ? w : DefaultNewFeatureWeight;
            score += featureValue * weight;
        }

        // Apply sigmoid to get probability
        var probability = 1.0 / (1.0 + Math.Exp(-score));

        return (probability > 0.5, probability);
    }

    /// <summary>
    ///     Infers the bot type from active features and prior evidence.
    ///     In full mode, defers to the PrimaryBotType already determined by upstream detectors.
    ///     In early mode, checks which UA features fired to distinguish tools from scrapers.
    /// </summary>
    private static BotType InferBotType(Dictionary<string, float> features, AggregatedEvidence? evidence)
    {
        // Full mode: upstream detectors (UserAgent, VerifiedBot, etc.) already identified the type
        if (evidence?.PrimaryBotType is { } upstream and not BotType.Unknown)
            return upstream;

        // Early mode: infer from UA features
        // Tool UAs (curl, wget, python-requests, etc.) → BotType.Tool
        if (features.TryGetValue("ua:curl", out var curl) && curl > 0 ||
            features.TryGetValue("ua:wget", out var wget) && wget > 0 ||
            features.TryGetValue("ua:httpx", out var httpx) && httpx > 0 ||
            features.TryGetValue("ua:aiohttp", out var aiohttp) && aiohttp > 0 ||
            features.TryGetValue("ua:requests", out var requests) && requests > 0)
            return BotType.Tool;

        // Automation/scraping frameworks → BotType.Scraper
        if (features.TryGetValue("ua:scrapy", out var scrapy) && scrapy > 0 ||
            features.TryGetValue("ua:selenium", out var selenium) && selenium > 0 ||
            features.TryGetValue("ua:headless", out var headless) && headless > 0 ||
            features.TryGetValue("ua:phantomjs", out var phantomjs) && phantomjs > 0)
            return BotType.Scraper;

        // Default fallback
        return BotType.Scraper;
    }

    /// <summary>
    ///     Updates weights based on detection feedback.
    ///     Called by the learning system when ground truth is available.
    /// </summary>
    public async Task UpdateWeightsAsync(
        Dictionary<string, float> features,
        bool wasBot,
        double confidence,
        CancellationToken cancellationToken = default)
    {
        if (_weightStore == null)
        {
            _logger.LogDebug("No weight store available, skipping weight update");
            return;
        }

        if (!_options.AiDetection.Heuristic.EnableWeightLearning) return;

        if (confidence < _options.AiDetection.Heuristic.MinConfidenceForLearning) return;

        try
        {
            var updatedCount = 0;

            // Record observation for each feature that was active
            foreach (var (featureName, featureValue) in features)
                if (Math.Abs(featureValue) > 0.001)
                {
                    await _weightStore.RecordObservationAsync(
                        WeightSignatureType,
                        featureName,
                        wasBot,
                        confidence * featureValue, // Weight by feature activation
                        cancellationToken);
                    updatedCount++;
                }

            _logger.LogDebug(
                "Updated weights for {FeatureCount} active features (wasBot={WasBot}, confidence={Confidence:F2})",
                updatedCount, wasBot, confidence);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update weights in store");
        }
    }

    /// <summary>
    ///     Reloads weights from the store.
    ///     Call this after learning updates to pick up new values.
    /// </summary>
    public async Task ReloadWeightsAsync(CancellationToken cancellationToken = default)
    {
        if (_weightStore == null) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            await LoadWeightsFromStoreAsync(cancellationToken);
            _logger.LogInformation("Reloaded heuristic weights from store ({Count} weights)", _weights.Count);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    ///     Gets the current feature names that have weights (for debugging/introspection).
    /// </summary>
    public IReadOnlyList<string> GetFeatureNames()
    {
        return _weights.Keys.ToList();
    }

    /// <summary>
    ///     Gets the current weights as a dictionary (for debugging/introspection).
    /// </summary>
    public IReadOnlyDictionary<string, float> GetCurrentWeights()
    {
        return _weights.Count > 0 ? _weights : DefaultWeights;
    }

    /// <summary>
    ///     Gets the current bias (for debugging/introspection).
    /// </summary>
    public float GetCurrentBias()
    {
        return _initialized ? _bias : DefaultBias;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing) _initLock.Dispose();

        _disposed = true;
    }
}