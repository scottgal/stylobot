using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Endpoints;

/// <summary>
///     Training data export endpoints for ML model training.
///     Exports signatures, clusters, and country reputation data in JSON and JSONL formats.
///     Secured via optional API key and per-IP rate limiting.
/// </summary>
public static partial class TrainingDataEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions JsonlOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    // PII signal keys always excluded from training data
    private static readonly HashSet<string> PiiKeys =
    [
        SignalKeys.UserAgent,
        SignalKeys.ClientIp
    ];

    // UA classification keys - only included for bot-detected signatures
    private static readonly HashSet<string> UaClassificationKeys =
    [
        SignalKeys.UserAgentIsBot,
        SignalKeys.UserAgentBotType,
        SignalKeys.UserAgentBotName,
        SignalKeys.UserAgentOs,
        SignalKeys.UserAgentBrowser
    ];

    // Sliding window rate limiter: IP -> list of request timestamps
    private static readonly ConcurrentDictionary<string, List<DateTime>> RateLimitWindow = new();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^[0-9a-f\-]{8,}$|^\d{4,}$|^[A-Za-z0-9+/]{20,}={0,2}$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex IdSegmentPattern();

    /// <summary>
    ///     Maps training data export endpoints to the specified route prefix.
    ///     Applies API key validation and rate limiting based on <see cref="TrainingEndpointsOptions" />.
    /// </summary>
    public static RouteGroupBuilder MapBotTrainingEndpoints(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/bot-detection/training")
    {
        var group = endpoints.MapGroup(prefix)
            .WithTags("Bot Detection Training Data")
            .AddEndpointFilter(async (context, next) =>
            {
                var options = context.HttpContext.RequestServices
                    .GetService(typeof(IOptions<BotDetectionOptions>)) as IOptions<BotDetectionOptions>;
                var config = options?.Value.TrainingEndpoints ?? new TrainingEndpointsOptions();

                // Gate: endpoints disabled
                if (!config.Enabled)
                    return Results.NotFound();

                // Gate: API key required
                if (config.RequireApiKey)
                {
                    if (!context.HttpContext.Request.Headers.TryGetValue("X-Training-Api-Key", out var apiKey)
                        || !config.ApiKeys.Contains(apiKey.ToString()))
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetService(typeof(ILogger<BotDetectionOptions>)) as ILogger;
                        logger?.LogWarning("Training endpoint access denied: invalid or missing API key from {IP}",
                            context.HttpContext.Connection.RemoteIpAddress);
                        return Results.Json(new { error = "Valid X-Training-Api-Key header required" },
                            statusCode: StatusCodes.Status401Unauthorized);
                    }
                }

                // Gate: rate limiting
                if (config.RateLimitPerMinute > 0)
                {
                    var clientIp = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    if (!CheckRateLimit(clientIp, config.RateLimitPerMinute))
                    {
                        context.HttpContext.Response.Headers["Retry-After"] = "60";
                        return Results.Json(new { error = "Rate limit exceeded" },
                            statusCode: StatusCodes.Status429TooManyRequests);
                    }
                }

                context.HttpContext.Response.Headers["X-PII-Level"] = "none";
                context.HttpContext.Response.Headers["X-Data-Classification"] = "training-data";
                return await next(context);
            });

        group.MapGet("/signatures", ListSignatures)
            .WithName("ListTrainingSignatures")
            .WithSummary("List all tracked signatures with behavioral summaries");

        group.MapGet("/signatures/{signature}", GetSignatureDetail)
            .WithName("GetTrainingSignatureDetail")
            .WithSummary("Get full detail for one signature")
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/export", ExportJsonl)
            .WithName("ExportTrainingData")
            .WithSummary("JSONL streaming export of all signatures for ML training");

        group.MapGet("/clusters", ListClusters)
            .WithName("ListTrainingClusters")
            .WithSummary("Export all discovered bot clusters");

        group.MapGet("/countries", ListCountries)
            .WithName("ListTrainingCountries")
            .WithSummary("Export country reputation data");

        group.MapGet("/families", ListFamilies)
            .WithName("ListTrainingFamilies")
            .WithSummary("List all active signature families with members and aggregated stats");

        group.MapGet("/families/{familyId}", GetFamilyDetail)
            .WithName("GetTrainingFamilyDetail")
            .WithSummary("Get full detail for one signature family")
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/convergence/stats", GetConvergenceStats)
            .WithName("GetConvergenceStats")
            .WithSummary("Get convergence statistics: families, formation reasons, IP index");

        return group;
    }

    #region Rate Limiting

    private static bool CheckRateLimit(string clientIp, int maxPerMinute)
    {
        var now = DateTime.UtcNow;
        var window = RateLimitWindow.GetOrAdd(clientIp, _ => new List<DateTime>());

        lock (window)
        {
            // Evict entries older than 1 minute
            window.RemoveAll(t => (now - t).TotalMinutes > 1);
            if (window.Count >= maxPerMinute)
                return false;
            window.Add(now);
            return true;
        }
    }

    #endregion

    #region Derived Feature Computation

    /// <summary>
    ///     Computed features derived from a <see cref="SignatureBehavior" />.
    ///     Eliminates duplication across endpoints and BotClusterService.
    /// </summary>
    internal record DerivedFeatures(
        double DurationSeconds,
        double RequestRate,
        double PathDiversity,
        double IntervalStdDev);

    /// <summary>
    ///     Computes derived features from a signature behavior. Matches BotClusterService.BuildFeatureVectors logic.
    /// </summary>
    internal static DerivedFeatures ComputeDerived(SignatureBehavior b)
    {
        var durationSeconds = (b.LastSeen - b.FirstSeen).TotalSeconds;
        var requestRate = durationSeconds > 0 ? b.RequestCount / (durationSeconds / 60.0) : 0;

        var uniquePaths = b.Requests.Select(r => r.Path).Distinct().Count();
        var pathDiversity = b.RequestCount > 0 ? (double)uniquePaths / b.RequestCount : 0;

        var intervalStdDev = 0.0;
        if (b.Requests.Count > 2)
        {
            var intervals = new double[b.Requests.Count - 1];
            for (var i = 1; i < b.Requests.Count; i++)
                intervals[i - 1] = (b.Requests[i].Timestamp - b.Requests[i - 1].Timestamp).TotalSeconds;

            var avg = intervals.Average();
            intervalStdDev = Math.Sqrt(intervals.Average(v => Math.Pow(v - avg, 2)));
        }

        return new DerivedFeatures(durationSeconds, requestRate, pathDiversity, intervalStdDev);
    }

    #endregion

    #region Endpoint Handlers

    private static IResult ListSignatures(
        SignatureCoordinator signatureCoordinator,
        BotClusterService clusterService)
    {
        var behaviors = signatureCoordinator.GetAllBehaviors();

        var result = behaviors.Select(b =>
        {
            var cluster = clusterService.FindCluster(b.Signature);
            var spectral = clusterService.GetSpectralFeatures(b.Signature);
            var family = signatureCoordinator.GetFamily(b.Signature);
            var d = ComputeDerived(b);

            return new
            {
                signature = b.Signature,
                label = DeriveLabel(b.AverageBotProbability),
                vectors = BuildVectors(b, d, spectral),
                isAberrant = b.IsAberrant,
                cluster = FormatClusterSummary(cluster),
                family = family != null
                    ? new
                    {
                        familyId = family.FamilyId,
                        memberCount = family.MemberSignatures.Count,
                        formationReason = family.FormationReason.ToString(),
                        mergeConfidence = Math.Round(family.MergeConfidence, 4),
                        isCanonical = string.Equals(b.Signature, family.CanonicalSignature,
                            StringComparison.OrdinalIgnoreCase)
                    }
                    : null
            };
        }).ToList();

        return Results.Json(result, JsonOptions);
    }

    private static async Task<IResult> GetSignatureDetail(
        string signature,
        SignatureCoordinator signatureCoordinator,
        BotClusterService clusterService)
    {
        var behavior = await signatureCoordinator.GetSignatureBehaviorAsync(signature);
        if (behavior == null)
            return Results.NotFound(new { error = $"Signature '{signature}' not found" });

        var cluster = clusterService.FindCluster(signature);
        var spectral = clusterService.GetSpectralFeatures(signature);
        var family = signatureCoordinator.GetFamily(signature);
        var d = ComputeDerived(behavior);

        // Build family member details if in a family
        object? familyData = null;
        if (family != null)
        {
            var memberDetails = new List<object>();
            foreach (var memberSig in family.MemberSignatures.Keys)
            {
                var memberBehavior = await signatureCoordinator.GetSignatureBehaviorAsync(memberSig);
                memberDetails.Add(new
                {
                    signature = memberSig,
                    requestCount = memberBehavior?.RequestCount ?? 0,
                    averageBotProbability = memberBehavior != null
                        ? Math.Round(memberBehavior.AverageBotProbability, 4)
                        : 0.0,
                    isCanonical = string.Equals(memberSig, family.CanonicalSignature, StringComparison.OrdinalIgnoreCase)
                });
            }

            familyData = new
            {
                familyId = family.FamilyId,
                canonicalSignature = family.CanonicalSignature,
                memberCount = family.MemberSignatures.Count,
                formationReason = family.FormationReason.ToString(),
                mergeConfidence = Math.Round(family.MergeConfidence, 4),
                createdUtc = family.CreatedUtc,
                lastEvaluatedUtc = family.LastEvaluatedUtc,
                evaluationCount = family.EvaluationCount,
                members = memberDetails
            };
        }

        var result = new
        {
            signature = behavior.Signature,
            label = DeriveLabel(behavior.AverageBotProbability),
            vectors = BuildVectors(behavior, d, spectral),
            isAberrant = behavior.IsAberrant,
            cluster = cluster != null
                ? new
                {
                    clusterId = cluster.ClusterId,
                    type = cluster.Type.ToString(),
                    label = cluster.Label,
                    memberCount = cluster.MemberCount,
                    avgSimilarity = Math.Round(cluster.AverageSimilarity, 4),
                    temporalDensity = Math.Round(cluster.TemporalDensity, 4),
                    dominantCountry = cluster.DominantCountry,
                    dominantAsn = cluster.DominantAsn
                }
                : null,
            family = familyData,
            requests = behavior.Requests.Select(r => new
            {
                timestamp = r.Timestamp,
                path = GeneralizePath(r.Path),
                botProbability = Math.Round(r.BotProbability, 4),
                escalated = r.Escalated,
                detectorsRan = r.DetectorsRan.ToList(),
                signals = FilterPiiSignals(r.Signals,
                    isBotDetected: behavior.AverageBotProbability >= 0.5)
            }).ToList()
        };

        return Results.Json(result, JsonOptions);
    }

    private static async Task ExportJsonl(
        HttpContext httpContext,
        SignatureCoordinator signatureCoordinator,
        BotClusterService clusterService)
    {
        var options = httpContext.RequestServices
            .GetService(typeof(IOptions<BotDetectionOptions>)) as IOptions<BotDetectionOptions>;
        var maxRecords = options?.Value.TrainingEndpoints.MaxExportRecords ?? 10_000;

        httpContext.Response.ContentType = "application/x-ndjson";
        httpContext.Response.Headers["Content-Disposition"] = "attachment; filename=\"training-data.jsonl\"";

        var behaviors = signatureCoordinator.GetAllBehaviors();
        var count = 0;

        foreach (var b in behaviors)
        {
            if (++count > maxRecords)
                break;

            var cluster = clusterService.FindCluster(b.Signature);
            var spectral = clusterService.GetSpectralFeatures(b.Signature);
            var family = signatureCoordinator.GetFamily(b.Signature);
            var d = ComputeDerived(b);

            var record = new
            {
                signature = b.Signature,
                label = DeriveLabel(b.AverageBotProbability),
                // Behavioral vector
                v_timingRegularity = Math.Round(b.TimingCoefficient, 4),
                v_requestRate = Math.Round(d.RequestRate, 4),
                v_pathDiversity = Math.Round(d.PathDiversity, 4),
                v_pathEntropy = Math.Round(b.PathEntropy, 4),
                v_avgBotProbability = Math.Round(b.AverageBotProbability, 4),
                v_aberrationScore = Math.Round(b.AberrationScore, 4),
                // Temporal vector
                v_requestCount = b.RequestCount,
                v_durationSeconds = Math.Round(d.DurationSeconds, 1),
                v_averageInterval = Math.Round(b.AverageInterval, 3),
                v_intervalStdDev = Math.Round(d.IntervalStdDev, 3),
                // Spectral vector (null if insufficient data)
                v_dominantFrequency = spectral?.HasSufficientData == true
                    ? Math.Round(spectral.DominantFrequency, 6) : (double?)null,
                v_spectralEntropy = spectral?.HasSufficientData == true
                    ? Math.Round(spectral.SpectralEntropy, 4) : (double?)null,
                v_harmonicRatio = spectral?.HasSufficientData == true
                    ? Math.Round(spectral.HarmonicRatio, 4) : (double?)null,
                v_peakToAvgRatio = spectral?.HasSufficientData == true
                    ? Math.Round(spectral.PeakToAvgRatio, 4) : (double?)null,
                v_spectralCentroid = spectral?.HasSufficientData == true
                    ? Math.Round(spectral.SpectralCentroid, 4) : (double?)null,
                // Geo vector
                v_countryCode = b.CountryCode,
                v_asn = b.Asn,
                v_isDatacenter = b.IsDatacenter,
                // Cluster context
                isAberrant = b.IsAberrant,
                clusterType = cluster?.Type.ToString(),
                clusterLabel = cluster?.Label,
                familyId = family?.FamilyId,
                familyMemberCount = family?.MemberSignatures.Count,
                familyFormationReason = family?.FormationReason.ToString()
            };

            var line = JsonSerializer.Serialize(record, JsonlOptions);
            await httpContext.Response.WriteAsync(line + "\n");

            // Flush periodically to avoid buffering entire response
            if (count % 100 == 0)
                await httpContext.Response.Body.FlushAsync();
        }

        if (count > maxRecords)
        {
            var truncation = JsonSerializer.Serialize(
                new { _truncated = true, _maxRecords = maxRecords, _message = "Export capped. Increase MaxExportRecords to export more." },
                JsonlOptions);
            await httpContext.Response.WriteAsync(truncation + "\n");
        }
    }

    private static IResult ListClusters(BotClusterService clusterService)
    {
        var clusters = clusterService.GetClusters();
        return Results.Json(clusters, JsonOptions);
    }

    private static IResult ListCountries(CountryReputationTracker countryTracker)
    {
        var countries = countryTracker.GetAllCountries();
        return Results.Json(countries, JsonOptions);
    }

    private static async Task<IResult> ListFamilies(SignatureCoordinator signatureCoordinator)
    {
        var families = signatureCoordinator.GetAllFamilies();

        var result = new List<object>();
        foreach (var family in families)
        {
            var totalRequests = 0;
            var botProbSum = 0.0;

            foreach (var sig in family.MemberSignatures.Keys)
            {
                var behavior = await signatureCoordinator.GetSignatureBehaviorAsync(sig);
                if (behavior != null && behavior.RequestCount > 0)
                {
                    totalRequests += behavior.RequestCount;
                    botProbSum += behavior.AverageBotProbability * behavior.RequestCount;
                }
            }

            var aggregatedBotProb = totalRequests > 0 ? botProbSum / totalRequests : 0.0;

            result.Add(new
            {
                familyId = family.FamilyId,
                canonicalSignature = family.CanonicalSignature,
                memberCount = family.MemberSignatures.Count,
                memberSignatures = family.MemberSignatures.Keys.ToList(),
                formationReason = family.FormationReason.ToString(),
                mergeConfidence = Math.Round(family.MergeConfidence, 4),
                createdUtc = family.CreatedUtc,
                lastEvaluatedUtc = family.LastEvaluatedUtc,
                evaluationCount = family.EvaluationCount,
                aggregatedBotProbability = Math.Round(aggregatedBotProb, 4),
                totalRequestCount = totalRequests
            });
        }

        return Results.Json(result, JsonOptions);
    }

    private static async Task<IResult> GetFamilyDetail(
        string familyId,
        SignatureCoordinator signatureCoordinator)
    {
        var family = signatureCoordinator.GetAllFamilies()
            .FirstOrDefault(f => string.Equals(f.FamilyId, familyId, StringComparison.OrdinalIgnoreCase));

        if (family == null)
            return Results.NotFound(new { error = $"Family '{familyId}' not found" });

        var members = new List<object>();
        var totalRequests = 0;
        var botProbSum = 0.0;

        foreach (var sig in family.MemberSignatures.Keys)
        {
            var behavior = await signatureCoordinator.GetSignatureBehaviorAsync(sig);
            if (behavior != null && behavior.RequestCount > 0)
            {
                totalRequests += behavior.RequestCount;
                botProbSum += behavior.AverageBotProbability * behavior.RequestCount;

                members.Add(new
                {
                    signature = sig,
                    isCanonical = string.Equals(sig, family.CanonicalSignature, StringComparison.OrdinalIgnoreCase),
                    requestCount = behavior.RequestCount,
                    firstSeen = behavior.FirstSeen,
                    lastSeen = behavior.LastSeen,
                    averageInterval = Math.Round(behavior.AverageInterval, 3),
                    pathEntropy = Math.Round(behavior.PathEntropy, 4),
                    timingCoefficient = Math.Round(behavior.TimingCoefficient, 4),
                    averageBotProbability = Math.Round(behavior.AverageBotProbability, 4),
                    aberrationScore = Math.Round(behavior.AberrationScore, 4),
                    isAberrant = behavior.IsAberrant,
                    countryCode = behavior.CountryCode,
                    asn = behavior.Asn,
                    isDatacenter = behavior.IsDatacenter
                });
            }
            else
            {
                members.Add(new
                {
                    signature = sig,
                    isCanonical = string.Equals(sig, family.CanonicalSignature, StringComparison.OrdinalIgnoreCase),
                    requestCount = 0,
                    firstSeen = (DateTime?)null,
                    lastSeen = (DateTime?)null,
                    averageInterval = 0.0,
                    pathEntropy = 0.0,
                    timingCoefficient = 0.0,
                    averageBotProbability = 0.0,
                    aberrationScore = 0.0,
                    isAberrant = false,
                    countryCode = (string?)null,
                    asn = (string?)null,
                    isDatacenter = false
                });
            }
        }

        var aggregatedBotProb = totalRequests > 0 ? botProbSum / totalRequests : 0.0;

        var result = new
        {
            familyId = family.FamilyId,
            canonicalSignature = family.CanonicalSignature,
            memberCount = family.MemberSignatures.Count,
            formationReason = family.FormationReason.ToString(),
            mergeConfidence = Math.Round(family.MergeConfidence, 4),
            createdUtc = family.CreatedUtc,
            lastEvaluatedUtc = family.LastEvaluatedUtc,
            evaluationCount = family.EvaluationCount,
            aggregatedBotProbability = Math.Round(aggregatedBotProb, 4),
            totalRequestCount = totalRequests,
            members
        };

        return Results.Json(result, JsonOptions);
    }

    private static IResult GetConvergenceStats(SignatureCoordinator signatureCoordinator)
    {
        var families = signatureCoordinator.GetAllFamilies();
        var ipIndex = signatureCoordinator.GetIpIndex();

        var reasonBreakdown = families
            .GroupBy(f => f.FormationReason)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var familySizes = families.Select(f => f.MemberSignatures.Count).ToList();
        var mergeConfidences = families.Select(f => f.MergeConfidence).ToList();

        var ipSignatureCounts = ipIndex.Values.Select(v => v.Count).ToList();

        var result = new
        {
            totalFamilies = families.Count,
            reasonBreakdown,
            averageMergeConfidence = mergeConfidences.Count > 0
                ? Math.Round(mergeConfidences.Average(), 4) : 0.0,
            averageFamilySize = familySizes.Count > 0
                ? Math.Round(familySizes.Average(), 2) : 0.0,
            maxFamilySize = familySizes.Count > 0
                ? familySizes.Max() : 0,
            ipIndex = new
            {
                totalIpsTracked = ipIndex.Count,
                averageSignaturesPerIp = ipSignatureCounts.Count > 0
                    ? Math.Round(ipSignatureCounts.Average(), 2) : 0.0,
                maxSignaturesPerIp = ipSignatureCounts.Count > 0
                    ? ipSignatureCounts.Max() : 0
            }
        };

        return Results.Json(result, JsonOptions);
    }

    #endregion

    #region Shared Helpers

    /// <summary>
    ///     Builds the multi-vector representation used by /signatures and /signatures/{id} endpoints.
    /// </summary>
    private static object BuildVectors(SignatureBehavior b, DerivedFeatures d, SpectralFeatures? spectral)
    {
        return new
        {
            behavioral = new
            {
                timingRegularity = Math.Round(b.TimingCoefficient, 4),
                requestRate = Math.Round(d.RequestRate, 4),
                pathDiversity = Math.Round(d.PathDiversity, 4),
                pathEntropy = Math.Round(b.PathEntropy, 4),
                averageBotProbability = Math.Round(b.AverageBotProbability, 4),
                aberrationScore = Math.Round(b.AberrationScore, 4)
            },
            temporal = new
            {
                requestCount = b.RequestCount,
                firstSeen = b.FirstSeen,
                lastSeen = b.LastSeen,
                durationSeconds = Math.Round(d.DurationSeconds, 1),
                averageInterval = Math.Round(b.AverageInterval, 3),
                intervalStdDev = Math.Round(d.IntervalStdDev, 3)
            },
            spectral = spectral?.HasSufficientData == true
                ? new
                {
                    dominantFrequency = Math.Round(spectral.DominantFrequency, 6),
                    spectralEntropy = Math.Round(spectral.SpectralEntropy, 4),
                    harmonicRatio = Math.Round(spectral.HarmonicRatio, 4),
                    peakToAvgRatio = Math.Round(spectral.PeakToAvgRatio, 4),
                    spectralCentroid = Math.Round(spectral.SpectralCentroid, 4)
                }
                : null,
            geo = new
            {
                countryCode = b.CountryCode,
                asn = b.Asn,
                isDatacenter = b.IsDatacenter
            }
        };
    }

    private static object? FormatClusterSummary(BotCluster? cluster)
    {
        if (cluster == null) return null;
        return new
        {
            clusterId = cluster.ClusterId,
            type = cluster.Type.ToString(),
            label = cluster.Label,
            memberCount = cluster.MemberCount,
            avgSimilarity = Math.Round(cluster.AverageSimilarity, 4)
        };
    }

    internal static string DeriveLabel(double averageBotProbability) =>
        averageBotProbability >= 0.7 ? "bot"
        : averageBotProbability <= 0.3 ? "human"
        : "uncertain";

    internal static Dictionary<string, object>? FilterPiiSignals(
        IReadOnlyDictionary<string, object>? signals,
        bool isBotDetected = false)
    {
        if (signals == null || signals.Count == 0)
            return null;

        var blocked = new HashSet<string>(PiiKeys);

        // UA classification keys are only included for bot-detected signatures
        if (!isBotDetected)
            foreach (var key in UaClassificationKeys)
                blocked.Add(key);

        var filtered = signals
            .Where(kv => !blocked.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return filtered.Count > 0 ? filtered : null;
    }

    internal static string GeneralizePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "/";

        // Strip query string (may contain tokens/user IDs)
        var cleanPath = path.Split('?')[0];

        var segments = cleanPath.Split('/');
        var generalized = string.Join("/", segments.Select(s =>
            // Replace segments that look like IDs (GUIDs, numbers, base64)
            IdSegmentPattern().IsMatch(s) ? "*" : s));

        return generalized;
    }

    #endregion
}
