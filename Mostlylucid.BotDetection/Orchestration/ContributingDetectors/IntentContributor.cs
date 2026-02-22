using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Similarity;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Intent / threat scoring contributor — produces a unified threat score (0-1)
///     orthogonal to the bot probability score. A human probing .env files has low
///     bot score but high threat score. A verified Googlebot has high bot score but
///     zero threat score.
///
///     Two-layer approach:
///     1. Build an intent vector from session activity signals
///     2. Query the intent HNSW index for nearest known patterns
///     3. If no match or ambiguous → queue for LLM classification (handled separately)
///     4. Fallback heuristic from attack signal severity
///
///     Configuration loaded from: intent.detector.yaml
/// </summary>
public class IntentContributor : ConfiguredContributorBase
{
    private readonly IIntentSimilaritySearch _intentSearch;
    private readonly IntentVectorizer _vectorizer;
    private readonly ILogger<IntentContributor> _logger;

    public IntentContributor(
        ILogger<IntentContributor> logger,
        IntentVectorizer vectorizer,
        IIntentSimilaritySearch intentSearch,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _vectorizer = vectorizer;
        _intentSearch = intentSearch;
    }

    public override string Name => "Intent";
    public override int Priority => 40; // After StreamAbuse=35, before Heuristic=50

    public override IReadOnlyList<TriggerCondition> TriggerConditions => new TriggerCondition[]
    {
        Triggers.AnyOf(
            new SignalExistsTrigger(SignalKeys.AttackDetected),
            new SignalExistsTrigger(SignalKeys.ResponseHasHistory),
            new SignalExistsTrigger(SignalKeys.StreamAbuseChecked),
            new SignalExistsTrigger(SignalKeys.TransportProtocol))
    };

    // Config-driven parameters from YAML
    private double SimilarityThreshold => GetParam("similarity_threshold", 0.75);
    private int TopK => GetParam("top_k", 5);
    private double AmbiguousLow => GetParam("ambiguous_low", 0.3);
    private double AmbiguousHigh => GetParam("ambiguous_high", 0.7);

    // Fallback heuristic weights per attack type
    private double FallbackInjection => GetParam("fallback_injection", 0.85);
    private double FallbackScanning => GetParam("fallback_scanning", 0.6);
    private double FallbackHoneypot => GetParam("fallback_honeypot", 0.9);
    private double FallbackAuthAbuse => GetParam("fallback_auth_abuse", 0.7);
    private double FallbackClean => GetParam("fallback_clean", 0.05);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        return ContributeInternalAsync(state, cancellationToken);
    }

    private async Task<IReadOnlyList<DetectionContribution>> ContributeInternalAsync(
        BlackboardState state, CancellationToken ct)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            state.WriteSignal(SignalKeys.IntentAnalyzed, true);

            // Build intent features from blackboard signals
            var features = BuildIntentFeatures(state);

            // Vectorize
            var vector = _vectorizer.Vectorize(features);

            // Query intent HNSW index
            var matches = await _intentSearch.FindSimilarAsync(
                vector, TopK, (float)SimilarityThreshold);

            double threatScore;
            string intentCategory;

            if (matches.Count > 0)
            {
                // Weighted average from top-K matches (closer = more weight)
                threatScore = ComputeWeightedThreatScore(matches);
                intentCategory = matches[0].IntentCategory; // Use closest match category

                state.WriteSignal(SignalKeys.IntentSimilarityScore, (double)(1.0f - matches[0].Distance));
                state.WriteSignal(SignalKeys.IntentMatchCount, matches.Count);

                _logger.LogDebug(
                    "Intent HNSW match: {Count} matches, top similarity={Sim:F3}, threat={Threat:F3}, category={Cat}",
                    matches.Count, 1.0f - matches[0].Distance, threatScore, intentCategory);
            }
            else
            {
                // No HNSW match — use fallback heuristic from attack signals
                (threatScore, intentCategory) = ComputeHeuristicThreat(state);

                _logger.LogDebug(
                    "Intent: no HNSW match, heuristic threat={Threat:F3}, category={Cat}",
                    threatScore, intentCategory);
            }

            // Check ambiguity — queue for LLM if unclear
            var isAmbiguous = threatScore >= AmbiguousLow && threatScore <= AmbiguousHigh;
            if (isAmbiguous || matches.Count == 0)
            {
                state.WriteSignal(SignalKeys.IntentAmbiguous, true);
                // LLM classification is handled by IntentClassificationCoordinator
                // which reads this signal from the learning event
            }

            // Classify into threat band
            var band = ClassifyThreatBand(threatScore);

            // Write signals
            state.WriteSignal(SignalKeys.IntentThreatScore, threatScore);
            state.WriteSignal(SignalKeys.IntentThreatBand, band.ToString());
            state.WriteSignal(SignalKeys.IntentCategory, intentCategory);

            // Always return NeutralContribution — threat is orthogonal to bot probability
            contributions.Add(NeutralContribution("Intent",
                $"Session intent: {intentCategory} (threat={threatScore:F2}, band={band})"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in intent analysis");
            state.WriteSignal(SignalKeys.IntentThreatScore, 0.0);
            state.WriteSignal(SignalKeys.IntentThreatBand, ThreatBand.None.ToString());
            contributions.Add(NeutralContribution("Intent", "Intent analysis error"));
        }

        return contributions;
    }

    private Dictionary<string, float> BuildIntentFeatures(BlackboardState state)
    {
        var features = new Dictionary<string, float>();

        // Attack features from HaxxorContributor
        var attackDetected = state.GetSignal<bool?>(SignalKeys.AttackDetected) ?? false;
        var attackCategories = state.GetSignal<string>(SignalKeys.AttackCategories) ?? "";
        var attackSeverity = state.GetSignal<string>(SignalKeys.AttackSeverity) ?? "";

        var hasInjection = (state.GetSignal<bool?>(SignalKeys.AttackSqli) ?? false)
                           || (state.GetSignal<bool?>(SignalKeys.AttackXss) ?? false)
                           || (state.GetSignal<bool?>(SignalKeys.AttackCmdi) ?? false)
                           || (state.GetSignal<bool?>(SignalKeys.AttackSsti) ?? false)
                           || (state.GetSignal<bool?>(SignalKeys.AttackSsrf) ?? false);

        var hasScanning = (state.GetSignal<bool?>(SignalKeys.AttackPathProbe) ?? false)
                          || (state.GetSignal<bool?>(SignalKeys.AttackConfigExposure) ?? false)
                          || (state.GetSignal<bool?>(SignalKeys.AttackAdminScan) ?? false)
                          || (state.GetSignal<bool?>(SignalKeys.AttackWebshellProbe) ?? false)
                          || (state.GetSignal<bool?>(SignalKeys.AttackBackupScan) ?? false)
                          || (state.GetSignal<bool?>(SignalKeys.AttackDebugExposure) ?? false);

        features["attack:has_injection"] = hasInjection ? 1.0f : 0.0f;
        features["attack:has_scanning"] = hasScanning ? 1.0f : 0.0f;

        var categoryCount = string.IsNullOrEmpty(attackCategories) ? 0 : attackCategories.Split(',').Length;
        features["attack:category_count"] = Math.Min(categoryCount / 5.0f, 1.0f); // Normalize to 0-1

        features["attack:severity"] = attackSeverity switch
        {
            "critical" => 1.0f,
            "high" => 0.75f,
            "medium" => 0.5f,
            "low" => 0.25f,
            _ => 0.0f
        };

        // Response features from ResponseBehaviorContributor
        var count404 = state.GetSignal<int?>(SignalKeys.ResponseCount404) ?? 0;
        var unique404 = state.GetSignal<int?>(SignalKeys.ResponseUnique404Paths) ?? 0;
        var honeypotHits = state.GetSignal<int?>(SignalKeys.ResponseHoneypotHits) ?? 0;
        var authFailures = state.GetSignal<int?>(SignalKeys.ResponseAuthFailures) ?? 0;
        var errorHarvesting = state.GetSignal<bool?>(SignalKeys.ResponseErrorHarvesting) ?? false;
        var totalResponses = state.GetSignal<int?>(SignalKeys.ResponseTotalResponses) ?? 0;

        features["response:404_ratio"] = totalResponses > 0
            ? Math.Min((float)count404 / totalResponses, 1.0f) : 0.0f;
        features["response:honeypot_hits"] = Math.Min(honeypotHits / 3.0f, 1.0f);
        features["response:error_harvesting"] = errorHarvesting ? 1.0f : 0.0f;

        // Auth features
        features["auth:failure_count"] = Math.Min(authFailures / 10.0f, 1.0f);
        var bruteForce = (state.GetSignal<bool?>(SignalKeys.AtoBruteForce) ?? false)
                         || (state.GetSignal<bool?>(SignalKeys.AtoCredentialStuffing) ?? false);
        features["auth:brute_force"] = bruteForce ? 1.0f : 0.0f;

        // Transport features
        var transportClass = state.GetSignal<string>(SignalKeys.TransportClass) ?? "http";
        var isStreaming = state.GetSignal<bool?>(SignalKeys.TransportIsStreaming) ?? false;
        var protocolClass = state.GetSignal<string>(SignalKeys.TransportProtocolClass) ?? "unknown";

        features["transport:content_ratio"] = protocolClass == "document" ? 1.0f : 0.0f;
        features["transport:stream_ratio"] = isStreaming ? 1.0f : 0.0f;
        features["transport:api_ratio"] = protocolClass == "api" ? 1.0f : 0.0f;

        // Temporal features from BehavioralWaveform
        var burstDetected = state.GetSignal<bool?>(SignalKeys.WaveformBurstDetected) ?? false;
        var timingRegularity = state.GetSignal<double?>(SignalKeys.WaveformTimingRegularity) ?? 0.0;
        var pathDiversity = state.GetSignal<double?>(SignalKeys.WaveformPathDiversity) ?? 0.5;

        features["temporal:burst_ratio"] = burstDetected ? 1.0f : 0.0f;
        features["temporal:interrequest_cv"] = (float)Math.Min(timingRegularity, 1.0);

        // Behavioral features
        features["behavior:path_repetition"] = (float)(1.0 - Math.Min(pathDiversity, 1.0));

        // Stream abuse signals
        var handshakeStorm = state.GetSignal<bool?>(SignalKeys.StreamHandshakeStorm) ?? false;
        var crossMixing = state.GetSignal<bool?>(SignalKeys.StreamCrossEndpointMixing) ?? false;

        // Path classification based on current request path
        ClassifyPath(state.Path, features);

        return features;
    }

    private static void ClassifyPath(string path, Dictionary<string, float> features)
    {
        var lowerPath = path.ToLowerInvariant();

        // Simple path classification — probe paths get special treatment
        var isProbe = lowerPath.Contains(".env") || lowerPath.Contains("wp-admin") ||
                      lowerPath.Contains("phpmyadmin") || lowerPath.Contains(".git") ||
                      lowerPath.Contains("/.") || lowerPath.Contains("actuator") ||
                      lowerPath.Contains("phpinfo") || lowerPath.Contains("wp-login");
        var isAdmin = lowerPath.Contains("/admin") || lowerPath.Contains("/dashboard") ||
                      lowerPath.Contains("/cpanel") || lowerPath.Contains("/jenkins");
        var isAuth = lowerPath.Contains("/login") || lowerPath.Contains("/signin") ||
                     lowerPath.Contains("/auth") || lowerPath.Contains("/token") ||
                     lowerPath.Contains("/oauth");
        var isApi = lowerPath.StartsWith("/api/") || lowerPath.Contains("/graphql");
        var isStatic = lowerPath.EndsWith(".js") || lowerPath.EndsWith(".css") ||
                       lowerPath.EndsWith(".png") || lowerPath.EndsWith(".jpg") ||
                       lowerPath.EndsWith(".svg") || lowerPath.EndsWith(".ico") ||
                       lowerPath.EndsWith(".woff2");

        features["path:probe"] = isProbe ? 1.0f : 0.0f;
        features["path:admin"] = isAdmin ? 1.0f : 0.0f;
        features["path:auth"] = isAuth ? 1.0f : 0.0f;
        features["path:api"] = isApi ? 1.0f : 0.0f;
        features["path:static"] = isStatic ? 1.0f : 0.0f;
        features["path:content"] = (!isProbe && !isAdmin && !isAuth && !isApi && !isStatic) ? 1.0f : 0.0f;
    }

    private static double ComputeWeightedThreatScore(IReadOnlyList<SimilarIntent> matches)
    {
        if (matches.Count == 0) return 0.0;

        double totalWeight = 0;
        double weightedSum = 0;

        foreach (var match in matches)
        {
            // Weight by similarity (closer = higher weight)
            var similarity = 1.0 - match.Distance;
            var weight = similarity * similarity; // Quadratic weighting
            weightedSum += match.ThreatScore * weight;
            totalWeight += weight;
        }

        return totalWeight > 0 ? weightedSum / totalWeight : 0.0;
    }

    private (double ThreatScore, string Category) ComputeHeuristicThreat(BlackboardState state)
    {
        var attackDetected = state.GetSignal<bool?>(SignalKeys.AttackDetected) ?? false;
        var honeypotHits = state.GetSignal<int?>(SignalKeys.ResponseHoneypotHits) ?? 0;
        var scanPattern = state.GetSignal<bool?>(SignalKeys.ResponseScanPatternDetected) ?? false;
        var authFailures = state.GetSignal<int?>(SignalKeys.ResponseAuthFailures) ?? 0;
        var bruteForce = state.GetSignal<bool?>(SignalKeys.AtoBruteForce) ?? false;
        var credentialStuffing = state.GetSignal<bool?>(SignalKeys.AtoCredentialStuffing) ?? false;

        // Check for injection attacks
        var hasInjection = (state.GetSignal<bool?>(SignalKeys.AttackSqli) ?? false)
                           || (state.GetSignal<bool?>(SignalKeys.AttackXss) ?? false)
                           || (state.GetSignal<bool?>(SignalKeys.AttackCmdi) ?? false)
                           || (state.GetSignal<bool?>(SignalKeys.AttackSsti) ?? false)
                           || (state.GetSignal<bool?>(SignalKeys.AttackSsrf) ?? false);

        var hasScanning = scanPattern
                          || (state.GetSignal<bool?>(SignalKeys.AttackPathProbe) ?? false)
                          || (state.GetSignal<bool?>(SignalKeys.AttackConfigExposure) ?? false)
                          || (state.GetSignal<bool?>(SignalKeys.AttackAdminScan) ?? false);

        // Priority ordering: honeypot > injection > scanning > auth abuse > clean
        if (honeypotHits > 0)
            return (FallbackHoneypot, "attacking");

        if (hasInjection)
            return (FallbackInjection, "attacking");

        if (bruteForce || credentialStuffing)
            return (FallbackAuthAbuse, "attacking");

        if (hasScanning)
            return (FallbackScanning, "scanning");

        if (attackDetected)
            return (FallbackScanning, "reconnaissance");

        return (FallbackClean, "browsing");
    }

    private static ThreatBand ClassifyThreatBand(double threatScore) =>
        threatScore switch
        {
            >= 0.80 => ThreatBand.Critical,
            >= 0.55 => ThreatBand.High,
            >= 0.35 => ThreatBand.Elevated,
            >= 0.15 => ThreatBand.Low,
            _ => ThreatBand.None
        };
}
