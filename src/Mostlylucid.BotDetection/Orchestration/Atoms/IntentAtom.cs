using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Similarity;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     RankerAtom (per Taxonomy.md) that produces a unified threat score
///     (0-1) orthogonal to bot probability. A human probing .env files has
///     low bot score but high threat score; a verified Googlebot has high
///     bot score but zero threat score.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>IntentContributor</c>. Priority 40 -- after StreamAbuse (35),
///         before Heuristic (50).
///     </para>
///     <para>
///         Two-layer approach preserved: build an intent feature vector,
///         query the intent HNSW index for nearest known patterns, fall back
///         to a heuristic from attack signal severity when no match, and
///         optionally queue ambiguous cases for LLM classification.
///     </para>
///     <para>
///         Trigger inlined: <see cref="SignalKeys.SessionRequestCount"/>
///         required + one of StreamAbuseChecked / ResponseHasHistory /
///         AttackDetected / ClickFraudChecked. RequiredSignals is
///         intersection-only; the OR arm evaluates at the top of DetectAsync.
///     </para>
/// </remarks>
public sealed class IntentAtom : DetectorAtomBase
{
    private readonly IIntentSimilaritySearch _intentSearch;
    private readonly IntentVectorizer _vectorizer;
    private readonly IntentClassificationCoordinator? _intentCoordinator;
    private readonly ILogger<IntentAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public IntentAtom(
        ILogger<IntentAtom> logger,
        IntentVectorizer vectorizer,
        IIntentSimilaritySearch intentSearch,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        IntentClassificationCoordinator? intentCoordinator = null)
        : base(name: "Intent", category: "Intent")
    {
        _logger = logger;
        _vectorizer = vectorizer;
        _intentSearch = intentSearch;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _intentCoordinator = intentCoordinator;
    }

    public override int Priority => 40;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.SessionRequestCount };

    private double SimilarityThreshold => _configProvider.GetParameter(Name, "similarity_threshold", 0.75);
    private int TopK => _configProvider.GetParameter(Name, "top_k", 5);
    private double AmbiguousLow => _configProvider.GetParameter(Name, "ambiguous_low", 0.3);
    private double AmbiguousHigh => _configProvider.GetParameter(Name, "ambiguous_high", 0.7);
    private double FallbackInjection => _configProvider.GetParameter(Name, "fallback_injection", 0.85);
    private double FallbackScanning => _configProvider.GetParameter(Name, "fallback_scanning", 0.6);
    private double FallbackHoneypot => _configProvider.GetParameter(Name, "fallback_honeypot", 0.9);
    private double FallbackAuthAbuse => _configProvider.GetParameter(Name, "fallback_auth_abuse", 0.7);
    private double FallbackClean => _configProvider.GetParameter(Name, "fallback_clean", 0.05);
    private double ClickFraudWeight => _configProvider.GetParameter(Name, "clickfraud_weight", 0.35);
    private double ClickFraudThreshold => _configProvider.GetParameter(Name, "clickfraud_threshold", 0.55);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        // OR-arm trigger evaluation: at least one of the pre-req partner
        // signals must be present alongside SessionRequestCount.
        var hasPartnerSignal = sink.ReadHint(SignalKeys.StreamAbuseChecked) is not null
                               || sink.ReadHint(SignalKeys.ResponseHasHistory) is not null
                               || sink.ReadHint(SignalKeys.AttackDetected) is not null
                               || sink.ReadHint(SignalKeys.ClickFraudChecked) is not null;
        if (!hasPartnerSignal) return None();

        sink.Raise("intent.ran", sessionId);

        var contributions = new List<DetectionContribution>(1);
        var context = _httpContextAccessor.HttpContext;
        var path = context?.Request.Path.Value ?? "/";

        try
        {
            sink.Raise($"{SignalKeys.IntentAnalyzed}:true", sessionId);

            var features = BuildIntentFeatures(sink, path);
            var vector = _vectorizer.Vectorize(features);

            var matches = await _intentSearch.FindSimilarAsync(vector, TopK, (float)SimilarityThreshold)
                .ConfigureAwait(false);

            double threatScore;
            string intentCategory;

            if (matches.Count > 0)
            {
                threatScore = ComputeWeightedThreatScore(matches);
                intentCategory = matches[0].IntentCategory;

                sink.Raise($"{SignalKeys.IntentSimilarityScore}:{((double)(1.0f - matches[0].Distance)).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}", sessionId);
                sink.Raise($"{SignalKeys.IntentMatchCount}:{matches.Count}", sessionId);

                _logger.LogDebug(
                    "Intent HNSW match: {Count} matches, top similarity={Sim:F3}, threat={Threat:F3}, category={Cat}",
                    matches.Count, 1.0f - matches[0].Distance, threatScore, intentCategory);
            }
            else
            {
                (threatScore, intentCategory) = ComputeHeuristicThreat(sink);
                _logger.LogDebug("Intent: no HNSW match, heuristic threat={Threat:F3}, category={Cat}",
                    threatScore, intentCategory);
            }

            var isAmbiguous = threatScore >= AmbiguousLow && threatScore <= AmbiguousHigh;
            if (isAmbiguous || matches.Count == 0)
            {
                sink.Raise($"{SignalKeys.IntentAmbiguous}:true", sessionId);

                if (_intentCoordinator is not null)
                {
                    // Snapshot the sink into a signals dict so the LLM prompt
                    // builder can render session context. Values are strings
                    // (Model-2 hints); the coordinator consumes them for
                    // display, not typed reads.
                    var snapshotSignals = SnapshotSinkAsSignalDict(sink);

                    var enqueued = _intentCoordinator.TryEnqueue(new IntentClassificationRequest
                    {
                        RequestId = sessionId,
                        PrimarySignature = sink.ReadHint(SignalKeys.PrimarySignature) ?? sessionId,
                        IntentVector = vector,
                        IntentFeatures = features,
                        Signals = snapshotSignals,
                        SessionSummary = IntentPromptBuilder.BuildSessionSummary(snapshotSignals, path),
                        HeuristicThreatScore = threatScore
                    });

                    if (!enqueued)
                    {
                        _logger.LogDebug(
                            "Intent classification queue full for {RequestId}; using heuristic-only threat score",
                            sessionId);
                    }
                }
            }

            var band = ClassifyThreatBand(threatScore);
            sink.Raise($"{SignalKeys.IntentThreatScore}:{threatScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}", sessionId);
            sink.Raise($"{SignalKeys.IntentThreatBand}:{band}", sessionId);
            sink.Raise($"{SignalKeys.IntentCategory}:{intentCategory}", sessionId);

            contributions.Add(DetectionContribution.Info(Name, Category,
                $"Session intent: {intentCategory} (threat={threatScore:F2}, band={band})"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in intent analysis");
            sink.Raise($"{SignalKeys.IntentThreatScore}:0", sessionId);
            sink.Raise($"{SignalKeys.IntentThreatBand}:{ThreatBand.None}", sessionId);
            contributions.Add(DetectionContribution.Info(Name, Category, "Intent analysis error"));
        }

        return contributions;
    }

    private static IReadOnlyDictionary<string, object> SnapshotSinkAsSignalDict(SignalSink sink)
    {
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        var signals = sink.Sense(_ => true);
        for (var i = 0; i < signals.Count; i++)
        {
            var raw = signals[i].Signal;
            var colon = raw.IndexOf(':');
            if (colon <= 0)
                dict.TryAdd(raw, true);
            else
                dict.TryAdd(raw[..colon], raw[(colon + 1)..]);
        }
        return dict;
    }

    private Dictionary<string, float> BuildIntentFeatures(SignalSink sink, string path)
    {
        var features = new Dictionary<string, float>(53);

        // Attack features
        var attackCategories = sink.ReadHint(SignalKeys.AttackCategories) ?? string.Empty;
        var attackSeverity = sink.ReadHint(SignalKeys.AttackSeverity) ?? string.Empty;

        var hasInjection = sink.ReadBoolHint(SignalKeys.AttackSqli)
                           || sink.ReadBoolHint(SignalKeys.AttackXss)
                           || sink.ReadBoolHint(SignalKeys.AttackCmdi)
                           || sink.ReadBoolHint(SignalKeys.AttackSsti)
                           || sink.ReadBoolHint(SignalKeys.AttackSsrf);

        var hasScanning = sink.ReadBoolHint(SignalKeys.AttackPathProbe)
                          || sink.ReadBoolHint(SignalKeys.AttackConfigExposure)
                          || sink.ReadBoolHint(SignalKeys.AttackAdminScan)
                          || sink.ReadBoolHint(SignalKeys.AttackWebshellProbe)
                          || sink.ReadBoolHint(SignalKeys.AttackBackupScan)
                          || sink.ReadBoolHint(SignalKeys.AttackDebugExposure);

        features["attack:has_injection"] = hasInjection ? 1.0f : 0.0f;
        features["attack:has_scanning"] = hasScanning ? 1.0f : 0.0f;

        var categoryCount = 0;
        if (!string.IsNullOrEmpty(attackCategories))
        {
            categoryCount = 1;
            foreach (var c in attackCategories.AsSpan())
                if (c == ',') categoryCount++;
        }
        features["attack:category_count"] = Math.Min(categoryCount / 5.0f, 1.0f);
        features["attack:severity"] = attackSeverity switch
        {
            "critical" => 1.0f,
            "high" => 0.75f,
            "medium" => 0.5f,
            "low" => 0.25f,
            _ => 0.0f
        };

        var count404 = sink.ReadIntHint(SignalKeys.ResponseCount404);
        var honeypotHits = sink.ReadIntHint(SignalKeys.ResponseHoneypotHits);
        var authFailures = sink.ReadIntHint(SignalKeys.ResponseAuthFailures);
        var rateLimitViolations = sink.ReadIntHint(SignalKeys.ResponseRateLimitViolations);
        var errorHarvesting = sink.ReadBoolHint(SignalKeys.ResponseErrorHarvesting);
        var totalResponses = sink.ReadIntHint(SignalKeys.ResponseTotalResponses);

        features["response:4xx_ratio"] = totalResponses > 0
            ? Math.Min((float)(count404 + authFailures + rateLimitViolations) / totalResponses, 1.0f)
            : 0.0f;
        features["response:404_ratio"] = totalResponses > 0
            ? Math.Min((float)count404 / totalResponses, 1.0f) : 0.0f;
        features["response:honeypot_hits"] = Math.Min(honeypotHits / 3.0f, 1.0f);
        features["response:error_harvesting"] = errorHarvesting ? 1.0f : 0.0f;

        features["auth:failure_count"] = Math.Min(authFailures / 10.0f, 1.0f);
        var bruteForce = sink.ReadBoolHint(SignalKeys.AtoBruteForce)
                         || sink.ReadBoolHint(SignalKeys.AtoCredentialStuffing);
        features["auth:brute_force"] = bruteForce ? 1.0f : 0.0f;

        var isStreaming = sink.ReadBoolHint(SignalKeys.TransportIsStreaming);
        var protocolClass = sink.ReadHint(SignalKeys.TransportProtocolClass) ?? "unknown";
        features["transport:content_ratio"] = protocolClass == "document" ? 1.0f : 0.0f;
        features["transport:stream_ratio"] = isStreaming ? 1.0f : 0.0f;
        features["transport:api_ratio"] = protocolClass == "api" ? 1.0f : 0.0f;

        var burstDetected = sink.ReadBoolHint(SignalKeys.WaveformBurstDetected);
        var timingRegularity = sink.ReadDoubleHint(SignalKeys.WaveformTimingRegularity);
        var pathDiversity = sink.ReadDoubleHint(SignalKeys.WaveformPathDiversity, 0.5);
        var requestRate = sink.ReadDoubleHint("waveform.request_rate");
        var sessionDurationMinutes = sink.ReadDoubleHint("waveform.session_duration_minutes");
        var traversalPattern = sink.ReadHint("waveform.traversal_pattern") ?? "mixed";

        features["temporal:request_rate"] = (float)Math.Min(requestRate / 30.0, 1.0);
        features["temporal:burst_ratio"] = burstDetected ? 1.0f : 0.0f;
        features["temporal:interrequest_cv"] = (float)Math.Min(timingRegularity, 1.0);
        features["temporal:session_duration"] = (float)Math.Min(sessionDurationMinutes / 30.0, 1.0);

        features["path:unique_ratio"] = (float)Math.Min(pathDiversity, 1.0);
        features["behavior:path_repetition"] = (float)(1.0 - Math.Min(pathDiversity, 1.0));
        features["behavior:depth_pattern"] = traversalPattern switch
        {
            "depth-first-strict" => 1.0f,
            "depth-first-loose" => 0.7f,
            "mixed" => 0.25f,
            _ => 0.0f
        };

        var handshakeStorm = sink.ReadBoolHint(SignalKeys.StreamHandshakeStorm);
        var crossMixing = sink.ReadBoolHint(SignalKeys.StreamCrossEndpointMixing);
        var reconnectRate = sink.ReadDoubleHint(SignalKeys.StreamReconnectRate);
        var concurrentStreams = sink.ReadIntHint(SignalKeys.StreamConcurrentStreams);

        features["stream:handshake_storm"] = handshakeStorm ? 1.0f : 0.0f;
        features["stream:cross_endpoint_mixing"] = crossMixing ? 1.0f : 0.0f;
        features["stream:reconnect_rate"] = (float)Math.Min(reconnectRate / 20.0, 1.0);
        features["stream:concurrent_streams"] = Math.Min(concurrentStreams / 5.0f, 1.0f);

        // Session analytics
        var isVoid = sink.ReadBoolHint(SignalKeys.SessionIsVoid);
        var topSimilarity = (float)sink.ReadDoubleHint(SignalKeys.SessionTopSimilarity, 1.0);
        var periodicityScore = (float)sink.ReadDoubleHint(SignalKeys.SessionFrequencyPeriodicityScore);
        var inAttackCluster = sink.ReadBoolHint(SignalKeys.SessionTrajectoryInAttackCluster);
        var trajSimilarity = (float)sink.ReadDoubleHint(SignalKeys.SessionTrajectoryClusterSimilarity);
        // SessionDriftVector is a rich float[] on HttpContext.Items historically; under the pack
        // the vector doesn't leak to the sink. Use magnitude 0 when unavailable.
        var driftMagnitude = 0f;

        var mahalDist = (float)sink.ReadDoubleHint(SignalKeys.SessionMahalanobisNearestDistance, float.MaxValue);
        var hasCentroids = mahalDist != float.MaxValue;
        var isDeepVoid = isVoid && hasCentroids && mahalDist > 3.0f;

        features["session:is_void"] = isVoid ? 1.0f : 0.0f;
        features["session:is_deep_void"] = isDeepVoid ? 1.0f : 0.0f;
        features["session:novelty"] = isVoid ? 1.0f : Math.Max(0.0f, 1.0f - topSimilarity);
        features["session:mahalanobis_distance"] = hasCentroids ? Math.Min(mahalDist / 5.0f, 1.0f) : 0.0f;
        features["session:periodicity"] = periodicityScore;
        features["session:trajectory_in_attack"] = inAttackCluster ? 1.0f : 0.0f;
        features["session:trajectory_similarity"] = trajSimilarity;
        features["session:drift_magnitude"] = Math.Min(driftMagnitude / 2.0f, 1.0f);

        var complianceRatio = (float)sink.ReadDoubleHint(SignalKeys.ReactiveRetryAfterCompliance, -1);
        var pathPersistence = (float)sink.ReadDoubleHint(SignalKeys.ReactivePathPersistencePost403);
        var geoCv = (float)sink.ReadDoubleHint(SignalKeys.ReactiveGeometricRatioCv, -1);
        var rateAdapted = (float)sink.ReadDoubleHint(SignalKeys.ReactiveRateAdapted);
        var coordinatedRetry = (float)sink.ReadDoubleHint(SignalKeys.ReactiveCoordinatedRetry);

        features["reactive:compliance_precision"] = complianceRatio >= 0
            ? Math.Max(0f, 1f - Math.Abs(complianceRatio - 1f))
            : 0f;
        features["reactive:path_persistence"] = pathPersistence;
        features["reactive:geometric_backoff"] = geoCv >= 0
            ? Math.Max(0f, 1f - (float)(geoCv / 0.25))
            : 0f;
        features["reactive:rate_adapted"] = rateAdapted;
        features["reactive:coordinated_retry"] = coordinatedRetry;

        var clickFraudConfidence = sink.ReadDoubleHint(SignalKeys.ClickFraudConfidence);
        var clickFraudIsPaidTraffic = sink.ReadBoolHint(SignalKeys.ClickFraudIsPaidTraffic);
        features["clickfraud:confidence"] = (float)Math.Min(clickFraudConfidence, 1.0);
        features["clickfraud:is_paid_traffic"] = clickFraudIsPaidTraffic ? 1.0f : 0.0f;

        ClassifyPath(path, features);
        return features;
    }

    private static void ClassifyPath(string path, Dictionary<string, float> features)
    {
        var isProbe = path.Contains(".env", StringComparison.OrdinalIgnoreCase)
                      || path.Contains("wp-admin", StringComparison.OrdinalIgnoreCase)
                      || path.Contains("phpmyadmin", StringComparison.OrdinalIgnoreCase)
                      || path.Contains(".git", StringComparison.OrdinalIgnoreCase)
                      || path.Contains("/.", StringComparison.OrdinalIgnoreCase)
                      || path.Contains("actuator", StringComparison.OrdinalIgnoreCase)
                      || path.Contains("phpinfo", StringComparison.OrdinalIgnoreCase)
                      || path.Contains("wp-login", StringComparison.OrdinalIgnoreCase);
        var isAdmin = path.Contains("/admin", StringComparison.OrdinalIgnoreCase)
                      || path.Contains("/dashboard", StringComparison.OrdinalIgnoreCase)
                      || path.Contains("/cpanel", StringComparison.OrdinalIgnoreCase)
                      || path.Contains("/jenkins", StringComparison.OrdinalIgnoreCase);
        var isAuth = path.Contains("/login", StringComparison.OrdinalIgnoreCase)
                     || path.Contains("/signin", StringComparison.OrdinalIgnoreCase)
                     || path.Contains("/auth", StringComparison.OrdinalIgnoreCase)
                     || path.Contains("/token", StringComparison.OrdinalIgnoreCase)
                     || path.Contains("/oauth", StringComparison.OrdinalIgnoreCase);
        var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("/graphql", StringComparison.OrdinalIgnoreCase);
        var isStatic = path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase);

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
            var similarity = 1.0 - match.Distance;
            var weight = similarity * similarity;
            weightedSum += match.ThreatScore * weight;
            totalWeight += weight;
        }
        return totalWeight > 0 ? weightedSum / totalWeight : 0.0;
    }

    private (double ThreatScore, string Category) ComputeHeuristicThreat(SignalSink sink)
    {
        var attackDetected = sink.ReadBoolHint(SignalKeys.AttackDetected);
        var honeypotHits = sink.ReadIntHint(SignalKeys.ResponseHoneypotHits);
        var scanPattern = sink.ReadBoolHint(SignalKeys.ResponseScanPatternDetected);
        var bruteForce = sink.ReadBoolHint(SignalKeys.AtoBruteForce);
        var credentialStuffing = sink.ReadBoolHint(SignalKeys.AtoCredentialStuffing);

        var hasInjection = sink.ReadBoolHint(SignalKeys.AttackSqli)
                           || sink.ReadBoolHint(SignalKeys.AttackXss)
                           || sink.ReadBoolHint(SignalKeys.AttackCmdi)
                           || sink.ReadBoolHint(SignalKeys.AttackSsti)
                           || sink.ReadBoolHint(SignalKeys.AttackSsrf);

        var hasScanning = scanPattern
                          || sink.ReadBoolHint(SignalKeys.AttackPathProbe)
                          || sink.ReadBoolHint(SignalKeys.AttackConfigExposure)
                          || sink.ReadBoolHint(SignalKeys.AttackAdminScan);

        var clickFraudConfidence = sink.ReadDoubleHint(SignalKeys.ClickFraudConfidence);
        var clickFraudChecked = sink.ReadBoolHint(SignalKeys.ClickFraudChecked);

        if (honeypotHits > 0) return (FallbackHoneypot, "attacking");
        if (hasInjection) return (FallbackInjection, "attacking");
        if (bruteForce || credentialStuffing) return (FallbackAuthAbuse, "attacking");
        if (hasScanning) return (FallbackScanning, "scanning");
        if (attackDetected) return (FallbackScanning, "reconnaissance");

        if (clickFraudChecked && clickFraudConfidence > ClickFraudThreshold)
        {
            var fraudScore = Math.Min(clickFraudConfidence * ClickFraudWeight + ClickFraudThreshold, 1.0);
            return (fraudScore, "ad_fraud");
        }

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
